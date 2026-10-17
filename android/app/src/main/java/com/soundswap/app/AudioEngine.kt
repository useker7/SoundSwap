package com.soundswap.app

import android.annotation.SuppressLint
import android.content.Context
import android.media.AudioAttributes
import android.media.AudioDeviceInfo
import android.media.AudioFormat
import android.media.AudioManager
import android.media.AudioRecord
import android.media.AudioTrack
import android.media.MediaRecorder
import android.media.audiofx.AcousticEchoCanceler
import android.os.Build
import android.os.Process
import android.os.SystemClock
import java.io.IOException
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.SocketTimeoutException
import kotlin.math.abs

/**
 * PC ile ses alışverişini yapan motor.
 * İş parçacıkları: ağ alımı, HELLO (bağlantıyı canlı tutma), çalma (AudioTrack), mikrofon (AudioRecord).
 */
class AudioEngine(context: Context, val config: Config) {

    data class Config(
        val host: String,
        val port: Int = Protocol.PORT,
        val playPcAudio: Boolean,
        val sendMic: Boolean,
        val jitterMs: Int,
        val echoCancel: Boolean,
        /** 0 = otomatik */
        val inputDeviceId: Int,
    )

    private val audioManager = context.getSystemService(AudioManager::class.java)
    private val jitter = JitterBuffer(2, Protocol.SAMPLE_RATE, 500, config.jitterMs)
    private val socket = DatagramSocket().apply {
        soTimeout = 500
        receiveBufferSize = 256 * 1024
        try {
            trafficClass = 0xB8 // DSCP EF: Wi-Fi'da ses önceliği (WMM)
        } catch (_: Exception) {
        }
    }
    private val threads = mutableListOf<Thread>()
    @Volatile private var running = false
    @Volatile private var address: InetAddress? = null
    @Volatile private var lastAck = 0L

    // Ses yönlendirme durumunu geri yüklemek için
    private var communicationMode = false
    private var savedMode = AudioManager.MODE_NORMAL
    private var startedSco = false
    private var changedSpeakerphone = false

    // Arayüz için durum
    @Volatile var pcName: String? = null
        private set
    @Volatile var error: String? = null
        private set
    @Volatile var playPeak = 0f
        private set
    @Volatile var micPeak = 0f
        private set
    @Volatile var packetsReceived = 0L
        private set
    @Volatile var packetsLost = 0L
        private set
    @Volatile var micPacketsSent = 0L
        private set
    @Volatile var trackBufferMs = 0.0
        private set

    val connected: Boolean get() = SystemClock.elapsedRealtime() - lastAck < 3000
    val bufferedMs: Double get() = jitter.bufferedMs
    val underruns: Long get() = jitter.underruns
    val droppedFrames: Long get() = jitter.droppedFrames

    fun setJitterMs(ms: Int) = jitter.setTargetMs(ms)

    fun start() {
        running = true
        setupRouting()
        spawn("ss-net") { receiveLoop() }
        spawn("ss-hello") { helloLoop() }
        if (config.playPcAudio) spawn("ss-play") { playbackLoop() }
        if (config.sendMic) spawn("ss-mic") { micLoop() }
    }

    fun stop() {
        if (!running) return
        running = false
        val bye = Protocol.packet(Protocol.BYE)
        val a = address
        if (a != null) {
            // Ağ işlemi ana iş parçacığında yapılamaz.
            val t = Thread {
                try {
                    socket.send(DatagramPacket(bye, bye.size, a, config.port))
                } catch (_: Exception) {
                }
            }
            t.start()
            t.join(300)
        }
        socket.close()
        threads.forEach { it.join(1000) }
        threads.clear()
        restoreRouting()
    }

    private fun spawn(name: String, body: () -> Unit) {
        val t = Thread({
            try {
                body()
            } catch (e: Exception) {
                if (running) error = "${e.javaClass.simpleName}: ${e.message}"
            }
        }, name)
        threads += t
        t.start()
    }

    // ---- Ağ ----

    private fun helloLoop() {
        val flags = (if (config.playPcAudio) Protocol.FLAG_WANTS_PC_AUDIO else 0) or
            (if (config.sendMic) Protocol.FLAG_SENDS_MIC else 0)
        val payload = byteArrayOf(flags.toByte()) + Build.MODEL.toByteArray(Charsets.UTF_8)
        val pkt = Protocol.packet(Protocol.HELLO, payload)
        while (running) {
            var a = address
            if (a == null) {
                try {
                    a = InetAddress.getByName(config.host)
                    address = a
                } catch (e: Exception) {
                    error = "Could not resolve address: ${config.host}"
                    sleep(1000)
                    continue
                }
            }
            try {
                socket.send(DatagramPacket(pkt, pkt.size, a, config.port))
                if (error?.startsWith("Network") == true) error = null
            } catch (e: IOException) {
                if (running) error = "Network error: ${e.message}"
            }
            sleep(500)
        }
    }

    private fun receiveLoop() {
        val buf = ByteArray(4096)
        val packet = DatagramPacket(buf, buf.size)
        val samples = ShortArray(2048)
        var lastSeq = 0
        var hasSeq = false
        while (running) {
            try {
                packet.setData(buf)
                socket.receive(packet)
            } catch (_: SocketTimeoutException) {
                continue
            } catch (e: IOException) {
                if (!running) break
                continue
            }
            val n = packet.length
            when (Protocol.typeOf(buf, n)) {
                Protocol.HELLO_ACK -> {
                    if (!connected) jitter.clear()
                    lastAck = SystemClock.elapsedRealtime()
                    pcName = String(buf, Protocol.HEADER_SIZE, n - Protocol.HEADER_SIZE, Charsets.UTF_8)
                }
                Protocol.PC_AUDIO -> {
                    val seq = Protocol.seqOf(buf)
                    if (hasSeq) {
                        val diff = seq - lastSeq
                        if (diff in -1000..0) continue // geç kalmış / yinelenen paket
                        if (diff > 1) packetsLost += (diff - 1).toLong()
                    }
                    lastSeq = seq
                    hasSeq = true
                    val count = minOf((n - Protocol.HEADER_SIZE) / 2, samples.size)
                    var o = Protocol.HEADER_SIZE
                    for (i in 0 until count) {
                        samples[i] = ((buf[o].toInt() and 0xff) or (buf[o + 1].toInt() shl 8)).toShort()
                        o += 2
                    }
                    jitter.write(samples, count - count % 2)
                    packetsReceived++
                }
            }
        }
    }

    // ---- Çalma ----

    private fun playbackLoop() {
        Process.setThreadPriority(Process.THREAD_PRIORITY_URGENT_AUDIO)
        val burst = audioManager.getProperty(AudioManager.PROPERTY_OUTPUT_FRAMES_PER_BUFFER)
            ?.toIntOrNull()?.coerceIn(64, 960) ?: 240
        val minBuf = AudioTrack.getMinBufferSize(
            Protocol.SAMPLE_RATE, AudioFormat.CHANNEL_OUT_STEREO, AudioFormat.ENCODING_PCM_16BIT
        )
        val attrs = AudioAttributes.Builder()
            .setUsage(if (communicationMode) AudioAttributes.USAGE_VOICE_COMMUNICATION else AudioAttributes.USAGE_MEDIA)
            .setContentType(AudioAttributes.CONTENT_TYPE_MUSIC)
            .build()
        val track = AudioTrack.Builder()
            .setAudioAttributes(attrs)
            .setAudioFormat(
                AudioFormat.Builder()
                    .setSampleRate(Protocol.SAMPLE_RATE)
                    .setEncoding(AudioFormat.ENCODING_PCM_16BIT)
                    .setChannelMask(AudioFormat.CHANNEL_OUT_STEREO)
                    .build()
            )
            .setTransferMode(AudioTrack.MODE_STREAM)
            .setPerformanceMode(AudioTrack.PERFORMANCE_MODE_LOW_LATENCY)
            .setBufferSizeInBytes(maxOf(minBuf, burst * 4 * 8))
            .build()

        // Oboe'daki yöntem: tamponu 2 burst ile başlat, AudioTrack takılırsa büyüt.
        var bufFrames = track.setBufferSizeInFrames(burst * 2)
        trackBufferMs = bufFrames * 1000.0 / Protocol.SAMPLE_RATE
        var lastUnderruns = track.underrunCount
        var lastCheck = SystemClock.elapsedRealtime()

        val chunk = ShortArray(burst * 2)
        track.play()
        try {
            while (running) {
                jitter.read(chunk, chunk.size)
                var peak = 0
                for (s in chunk) peak = maxOf(peak, abs(s.toInt()))
                playPeak = maxOf(peak / 32768f, playPeak * 0.9f)

                var off = 0
                while (off < chunk.size && running) {
                    val w = track.write(chunk, off, chunk.size - off, AudioTrack.WRITE_BLOCKING)
                    if (w < 0) throw IOException("AudioTrack.write: $w")
                    off += w
                }

                val now = SystemClock.elapsedRealtime()
                if (now - lastCheck > 500) {
                    val u = track.underrunCount
                    if (u > lastUnderruns && bufFrames + burst <= track.bufferCapacityInFrames) {
                        bufFrames = track.setBufferSizeInFrames(bufFrames + burst)
                        trackBufferMs = bufFrames * 1000.0 / Protocol.SAMPLE_RATE
                    }
                    lastUnderruns = u
                    lastCheck = now
                }
            }
        } finally {
            track.pause()
            track.flush()
            track.release()
        }
    }

    // ---- Mikrofon ----

    @SuppressLint("MissingPermission")
    private fun micLoop() {
        Process.setThreadPriority(Process.THREAD_PRIORITY_URGENT_AUDIO)
        val frame = Protocol.MIC_FRAME_SAMPLES
        val minBuf = AudioRecord.getMinBufferSize(
            Protocol.SAMPLE_RATE, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT
        )
        val source = if (communicationMode) MediaRecorder.AudioSource.VOICE_COMMUNICATION else MediaRecorder.AudioSource.MIC
        val rec = AudioRecord.Builder()
            .setAudioSource(source)
            .setAudioFormat(
                AudioFormat.Builder()
                    .setSampleRate(Protocol.SAMPLE_RATE)
                    .setEncoding(AudioFormat.ENCODING_PCM_16BIT)
                    .setChannelMask(AudioFormat.CHANNEL_IN_MONO)
                    .build()
            )
            .setBufferSizeInBytes(maxOf(minBuf, frame * 2 * 8))
            .build()
        if (rec.state != AudioRecord.STATE_INITIALIZED) {
            rec.release()
            throw IOException("Could not open the microphone (is permission granted?)")
        }
        selectedInputDevice()?.let { rec.preferredDevice = it }
        val aec = if (config.echoCancel && AcousticEchoCanceler.isAvailable())
            AcousticEchoCanceler.create(rec.audioSessionId)?.apply { enabled = true } else null

        val pkt = ByteArray(Protocol.HEADER_SIZE + frame * 2)
        val samples = ShortArray(frame)
        var seq = 0
        rec.startRecording()
        try {
            while (running) {
                var got = 0
                while (got < frame && running) {
                    val r = rec.read(samples, got, frame - got)
                    if (r < 0) throw IOException("AudioRecord.read: $r")
                    got += r
                }
                if (got < frame) break

                var peak = 0
                var o = Protocol.HEADER_SIZE
                for (s in samples) {
                    val v = s.toInt()
                    pkt[o++] = v.toByte()
                    pkt[o++] = (v shr 8).toByte()
                    peak = maxOf(peak, abs(v))
                }
                micPeak = maxOf(peak / 32768f, micPeak * 0.9f)

                val a = address ?: continue
                if (!connected) continue
                Protocol.writeHeader(pkt, Protocol.MIC_AUDIO, seq++)
                try {
                    socket.send(DatagramPacket(pkt, pkt.size, a, config.port))
                    micPacketsSent++
                } catch (_: IOException) {
                }
            }
        } finally {
            rec.stop()
            aec?.release()
            rec.release()
        }
    }

    // ---- Ses yönlendirme ----

    private fun selectedInputDevice(): AudioDeviceInfo? =
        if (config.inputDeviceId == 0) null
        else audioManager.getDevices(AudioManager.GET_DEVICES_INPUTS).firstOrNull { it.id == config.inputDeviceId }

    private fun headsetConnected(): Boolean {
        val types = setOf(
            AudioDeviceInfo.TYPE_WIRED_HEADSET, AudioDeviceInfo.TYPE_WIRED_HEADPHONES,
            AudioDeviceInfo.TYPE_BLUETOOTH_A2DP, AudioDeviceInfo.TYPE_BLUETOOTH_SCO,
            AudioDeviceInfo.TYPE_USB_HEADSET, 26 /* TYPE_BLE_HEADSET */
        )
        return audioManager.getDevices(AudioManager.GET_DEVICES_OUTPUTS).any { it.type in types }
    }

    /**
     * Yankı engelleme veya Bluetooth kulaklık mikrofonu "iletişim modu" gerektirir.
     * Bu modda Android sesi ahizeye yönlendirdiği için, kulaklık yoksa hoparlöre çeviriyoruz.
     */
    @Suppress("DEPRECATION")
    private fun setupRouting() {
        val input = if (config.sendMic) selectedInputDevice() else null
        val useSco = input?.type == AudioDeviceInfo.TYPE_BLUETOOTH_SCO
        if (!(config.sendMic && config.echoCancel) && !useSco) return

        communicationMode = true
        savedMode = audioManager.mode
        audioManager.mode = AudioManager.MODE_IN_COMMUNICATION
        val wantSpeaker = !useSco && !headsetConnected()
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
            val devices = audioManager.availableCommunicationDevices
            val target = when {
                useSco -> devices.firstOrNull { it.type == AudioDeviceInfo.TYPE_BLUETOOTH_SCO }
                wantSpeaker -> devices.firstOrNull { it.type == AudioDeviceInfo.TYPE_BUILTIN_SPEAKER }
                else -> null
            }
            target?.let { audioManager.setCommunicationDevice(it) }
        } else {
            if (useSco) {
                audioManager.startBluetoothSco()
                audioManager.isBluetoothScoOn = true
                startedSco = true
            } else if (wantSpeaker) {
                audioManager.isSpeakerphoneOn = true
                changedSpeakerphone = true
            }
        }
    }

    @Suppress("DEPRECATION")
    private fun restoreRouting() {
        if (!communicationMode) return
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
            audioManager.clearCommunicationDevice()
        } else {
            if (startedSco) {
                audioManager.isBluetoothScoOn = false
                audioManager.stopBluetoothSco()
            }
            if (changedSpeakerphone) audioManager.isSpeakerphoneOn = false
        }
        audioManager.mode = savedMode
        communicationMode = false
    }

    private fun sleep(ms: Long) {
        try {
            Thread.sleep(ms)
        } catch (_: InterruptedException) {
        }
    }
}
