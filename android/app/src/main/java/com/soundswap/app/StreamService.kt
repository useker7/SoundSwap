package com.soundswap.app

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.net.wifi.WifiManager
import android.os.Build
import android.os.IBinder
import android.os.PowerManager

/**
 * Foreground service that keeps streaming while the screen is off / the app is in the background.
 * Holds a low-latency Wi-Fi lock, since Wi-Fi power saving adds a lot of latency.
 */
class StreamService : Service() {

    companion object {
        private const val CHANNEL_ID = "stream"
        private const val NOTIFICATION_ID = 1
        private const val ACTION_STOP = "com.soundswap.app.STOP"

        @Volatile var engine: AudioEngine? = null
            private set

        fun start(context: Context, config: AudioEngine.Config) {
            val i = Intent(context, StreamService::class.java)
                .putExtra("host", config.host)
                .putExtra("play", config.playPcAudio)
                .putExtra("mic", config.sendMic)
                .putExtra("jitter", config.jitterMs)
                .putExtra("aec", config.echoCancel)
                .putExtra("input", config.inputDeviceId)
            context.startForegroundService(i)
        }

        fun stop(context: Context) {
            context.stopService(Intent(context, StreamService::class.java))
        }
    }

    private var wifiLock: WifiManager.WifiLock? = null
    private var wakeLock: PowerManager.WakeLock? = null

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent == null || intent.action == ACTION_STOP) {
            stopSelf()
            return START_NOT_STICKY
        }
        val config = AudioEngine.Config(
            host = intent.getStringExtra("host") ?: "",
            playPcAudio = intent.getBooleanExtra("play", true),
            sendMic = intent.getBooleanExtra("mic", true),
            jitterMs = intent.getIntExtra("jitter", 30),
            echoCancel = intent.getBooleanExtra("aec", false),
            inputDeviceId = intent.getIntExtra("input", 0),
        )

        var type = ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PLAYBACK
        if (config.sendMic) type = type or ServiceInfo.FOREGROUND_SERVICE_TYPE_MICROPHONE
        val notification = buildNotification(config.host)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            startForeground(NOTIFICATION_ID, notification, type)
        } else {
            startForeground(NOTIFICATION_ID, notification)
        }

        acquireLocks()
        engine?.stop()
        engine = AudioEngine(this, config).also { it.start() }
        return START_NOT_STICKY
    }

    override fun onDestroy() {
        engine?.stop()
        engine = null
        releaseLocks()
        super.onDestroy()
    }

    @Suppress("DEPRECATION")
    private fun acquireLocks() {
        if (wifiLock == null) {
            val wm = applicationContext.getSystemService(WifiManager::class.java)
            val mode = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q)
                WifiManager.WIFI_MODE_FULL_LOW_LATENCY else WifiManager.WIFI_MODE_FULL_HIGH_PERF
            wifiLock = wm.createWifiLock(mode, "SoundSwap:wifi").apply {
                setReferenceCounted(false)
                acquire()
            }
        }
        if (wakeLock == null) {
            val pm = getSystemService(PowerManager::class.java)
            wakeLock = pm.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "SoundSwap:cpu").apply {
                setReferenceCounted(false)
                acquire()
            }
        }
    }

    private fun releaseLocks() {
        wifiLock?.release()
        wifiLock = null
        wakeLock?.release()
        wakeLock = null
    }

    private fun buildNotification(host: String): Notification {
        val nm = getSystemService(NotificationManager::class.java)
        nm.createNotificationChannel(
            NotificationChannel(CHANNEL_ID, getString(R.string.channel_name), NotificationManager.IMPORTANCE_LOW)
        )
        val open = PendingIntent.getActivity(
            this, 0, Intent(this, MainActivity::class.java), PendingIntent.FLAG_IMMUTABLE
        )
        val stop = PendingIntent.getService(
            this, 1, Intent(this, StreamService::class.java).setAction(ACTION_STOP), PendingIntent.FLAG_IMMUTABLE
        )
        return Notification.Builder(this, CHANNEL_ID)
            .setSmallIcon(R.drawable.ic_stat)
            .setContentTitle(getString(R.string.app_name))
            .setContentText(getString(R.string.notification_text, host))
            .setContentIntent(open)
            .setOngoing(true)
            .addAction(Notification.Action.Builder(null, getString(R.string.stop), stop).build())
            .build()
    }
}
