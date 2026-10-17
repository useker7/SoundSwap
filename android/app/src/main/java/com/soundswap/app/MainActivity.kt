package com.soundswap.app

import android.Manifest
import android.content.Context
import android.content.pm.PackageManager
import android.media.AudioDeviceCallback
import android.media.AudioDeviceInfo
import android.media.AudioManager
import android.os.Build
import android.os.Bundle
import android.widget.Toast
import androidx.activity.ComponentActivity
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Slider
import androidx.compose.material3.SliderDefaults
import androidx.compose.material3.Surface
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import com.soundswap.app.ui.Caption
import com.soundswap.app.ui.LocalExtra
import com.soundswap.app.ui.Logo
import com.soundswap.app.ui.SoundSwapTheme
import kotlinx.coroutines.delay
import kotlin.math.log10
import kotlin.math.roundToInt

class MainActivity : ComponentActivity() {
    private lateinit var discovery: PcDiscovery

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        discovery = PcDiscovery(this)
        setContent { SoundSwapTheme { SoundSwapScreen(discovery) } }
    }

    override fun onStart() {
        super.onStart()
        discovery.start()
    }

    override fun onStop() {
        discovery.stop()
        super.onStop()
    }
}

/** Engine state the UI reads 10 times per second. */
private data class EngineSnapshot(
    val running: Boolean = false,
    val connected: Boolean = false,
    val pcName: String? = null,
    val host: String = "",
    val error: String? = null,
    val playLevel: Float = 0f,
    val micLevel: Float = 0f,
    val latencyMs: Int = 0,
    val lost: Long = 0,
    val underruns: Long = 0,
) {
    companion object {
        fun of(e: AudioEngine?): EngineSnapshot = if (e == null) EngineSnapshot() else EngineSnapshot(
            running = true,
            connected = e.connected,
            pcName = e.pcName,
            host = e.config.host,
            error = e.error,
            playLevel = meter(e.playPeak),
            micLevel = meter(e.micPeak),
            latencyMs = (e.bufferedMs + e.trackBufferMs).roundToInt(),
            lost = e.packetsLost,
            underruns = e.underruns,
        )

        private fun meter(peak: Float): Float {
            if (peak <= 0.0001f) return 0f
            return ((20 * log10(peak) + 60) / 60).coerceIn(0f, 1f)
        }
    }
}

private data class InputOption(val id: Int, val label: String)

@Composable
private fun SoundSwapScreen(discovery: PcDiscovery) {
    val context = LocalContext.current
    val prefs = remember { context.getSharedPreferences("settings", Context.MODE_PRIVATE) }

    var play by remember { mutableStateOf(prefs.getBoolean("play", true)) }
    var mic by remember { mutableStateOf(prefs.getBoolean("mic", true)) }
    var echoCancel by remember { mutableStateOf(prefs.getBoolean("aec", false)) }
    var jitter by remember { mutableFloatStateOf(prefs.getInt("jitter", 30).toFloat()) }
    var inputId by remember { mutableIntStateOf(prefs.getInt("input", 0)) }
    val lastPc = remember { mutableStateOf(prefs.getString("host", "") ?: "") }
    var snap by remember { mutableStateOf(EngineSnapshot.of(StreamService.engine)) }
    var showManual by remember { mutableStateOf(false) }
    var pendingHost by remember { mutableStateOf<String?>(null) }

    LaunchedEffect(Unit) {
        while (true) {
            snap = EngineSnapshot.of(StreamService.engine)
            delay(100)
        }
    }

    fun save() {
        prefs.edit()
            .putBoolean("play", play).putBoolean("mic", mic).putBoolean("aec", echoCancel)
            .putInt("jitter", jitter.roundToInt()).putInt("input", inputId)
            .putString("host", lastPc.value)
            .apply()
    }

    fun hasMicPermission() =
        context.checkSelfPermission(Manifest.permission.RECORD_AUDIO) == PackageManager.PERMISSION_GRANTED

    fun launch(host: String) {
        lastPc.value = host
        save()
        StreamService.start(
            context,
            AudioEngine.Config(
                host = host,
                playPcAudio = play,
                sendMic = mic && hasMicPermission(),
                jitterMs = jitter.roundToInt(),
                echoCancel = echoCancel,
                inputDeviceId = inputId,
            )
        )
    }

    val permissionLauncher = rememberLauncherForActivityResult(
        ActivityResultContracts.RequestMultiplePermissions()
    ) { result ->
        if (result[Manifest.permission.RECORD_AUDIO] == false) {
            mic = false
            Toast.makeText(context, "Microphone permission denied; only PC audio will play.", Toast.LENGTH_LONG).show()
        }
        pendingHost?.let { launch(it) }
        pendingHost = null
    }

    fun connect(host: String) {
        val needed = buildList {
            if (mic && !hasMicPermission()) add(Manifest.permission.RECORD_AUDIO)
            if (Build.VERSION.SDK_INT >= 33 &&
                context.checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED &&
                !prefs.getBoolean("askedNotif", false)
            ) {
                add(Manifest.permission.POST_NOTIFICATIONS)
                prefs.edit().putBoolean("askedNotif", true).apply()
            }
        }
        if (needed.isEmpty()) launch(host) else {
            pendingHost = host
            permissionLauncher.launch(needed.toTypedArray())
        }
    }

    /** Apply settings changed while streaming (the engine restarts with the new settings). */
    fun applyWhileRunning() {
        save()
        StreamService.engine?.let { connect(it.config.host) }
    }

    val inputs = rememberInputOptions()

    Surface(Modifier.fillMaxSize(), color = MaterialTheme.colorScheme.background) {
        Column(
            Modifier
                .safeDrawingPadding()
                .verticalScroll(rememberScrollState())
                .padding(horizontal = 20.dp, vertical = 16.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            Header()

            if (snap.running) {
                ConnectionCard(snap, onDisconnect = { StreamService.stop(context) })
            } else {
                ComputerList(
                    pcs = discovery.pcs,
                    lastHost = lastPc.value,
                    onSelect = { connect(it) },
                    onManual = { showManual = true },
                )
            }

            SectionTitle("Audio")
            AppCard {
                SwitchRow(
                    icon = R.drawable.ic_speaker,
                    title = "Play PC audio",
                    subtitle = "Through the phone speaker or connected headphones",
                    checked = play,
                    onChange = { play = it; applyWhileRunning(); save() },
                )
                AnimatedVisibility(snap.running && play) { LevelBar(snap.playLevel, Modifier.padding(start = 52.dp, top = 12.dp)) }
                HorizontalDivider(Modifier.padding(vertical = 14.dp), color = MaterialTheme.colorScheme.outlineVariant)
                SwitchRow(
                    icon = R.drawable.ic_mic,
                    title = "Send microphone",
                    subtitle = "Shows up on the PC as “SoundSwap Microphone”",
                    checked = mic,
                    onChange = { mic = it; applyWhileRunning(); save() },
                )
                AnimatedVisibility(snap.running && mic) { LevelBar(snap.micLevel, Modifier.padding(start = 52.dp, top = 12.dp)) }
            }

            SectionTitle("Advanced")
            AppCard {
                InputPicker(
                    options = inputs,
                    selectedId = inputId,
                    enabled = mic,
                    onSelect = { inputId = it; applyWhileRunning(); save() },
                )
                HorizontalDivider(Modifier.padding(vertical = 14.dp), color = MaterialTheme.colorScheme.outlineVariant)
                SwitchRow(
                    icon = null,
                    title = "Echo cancellation",
                    subtitle = "Turn on when listening on the speaker and talking into the phone",
                    checked = echoCancel,
                    enabled = mic,
                    onChange = { echoCancel = it; applyWhileRunning(); save() },
                )
                HorizontalDivider(Modifier.padding(vertical = 14.dp), color = MaterialTheme.colorScheme.outlineVariant)
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Text("Audio buffer", style = MaterialTheme.typography.bodyLarge, modifier = Modifier.weight(1f))
                    Text("${jitter.roundToInt()} ms", style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
                Slider(
                    value = jitter,
                    onValueChange = {
                        jitter = (it / 5f).roundToInt() * 5f
                        StreamService.engine?.setJitterMs(jitter.roundToInt())
                    },
                    onValueChangeFinished = { save() },
                    valueRange = 10f..150f,
                    colors = SliderDefaults.colors(
                        thumbColor = MaterialTheme.colorScheme.primary,
                        activeTrackColor = MaterialTheme.colorScheme.primary,
                        inactiveTrackColor = LocalExtra.current.track,
                    ),
                )
                Text(
                    "Lower values mean less delay. Increase it if the audio stutters.",
                    style = Caption, color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }

            Text(
                "For the lowest latency use 5 GHz Wi-Fi and wired headphones. Bluetooth headphones add 100–250 ms on their own.",
                style = Caption,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                modifier = Modifier.padding(horizontal = 4.dp, vertical = 4.dp),
            )
        }
    }

    if (showManual) {
        ManualAddressDialog(
            initial = lastPc.value,
            onDismiss = { showManual = false },
            onConnect = { showManual = false; connect(it) },
        )
    }
}

// ---- Sections ----

@Composable
private fun Header() {
    Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.padding(top = 8.dp, bottom = 8.dp)) {
        Logo(44.dp)
        Spacer(Modifier.width(14.dp))
        Column {
            Text("SoundSwap", style = MaterialTheme.typography.titleLarge, color = MaterialTheme.colorScheme.onBackground)
            Text(
                "PC audio on your phone, phone mic on your PC",
                style = Caption, color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
    }
}

@Composable
private fun ComputerList(
    pcs: List<PcDiscovery.Pc>,
    lastHost: String,
    onSelect: (String) -> Unit,
    onManual: () -> Unit,
) {
    SectionTitle("PCs")
    AppCard(contentPadding = 0.dp) {
        if (pcs.isEmpty()) {
            Row(Modifier.padding(18.dp), verticalAlignment = Alignment.CenterVertically) {
                CircularProgressIndicator(Modifier.size(20.dp), strokeWidth = 2.dp)
                Spacer(Modifier.width(16.dp))
                Column {
                    Text("Looking for PCs…", style = MaterialTheme.typography.bodyLarge)
                    Text(
                        "SoundSwap must be running on the PC, and the PC must be on the same network as the phone.",
                        style = Caption, color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
            }
        } else {
            val sorted = pcs.sortedWith(compareByDescending<PcDiscovery.Pc> { it.address == lastHost }.thenBy { it.name.lowercase() })
            sorted.forEachIndexed { i, pc ->
                if (i > 0) HorizontalDivider(color = MaterialTheme.colorScheme.outlineVariant)
                Row(
                    Modifier
                        .fillMaxWidth()
                        .clickable { onSelect(pc.address) }
                        .padding(horizontal = 18.dp, vertical = 14.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    IconBadge(R.drawable.ic_computer)
                    Spacer(Modifier.width(14.dp))
                    Column(Modifier.weight(1f)) {
                        Text(pc.name, style = MaterialTheme.typography.titleMedium, maxLines = 1, overflow = TextOverflow.Ellipsis)
                        Text(
                            if (pc.address == lastHost) "${pc.address} · Last connected" else pc.address,
                            style = Caption, color = MaterialTheme.colorScheme.onSurfaceVariant,
                        )
                    }
                    Text("Connect", style = MaterialTheme.typography.labelLarge, color = MaterialTheme.colorScheme.primary)
                }
            }
        }
    }
    TextButton(onClick = onManual, modifier = Modifier.padding(start = 4.dp)) {
        Text("Enter address manually")
    }
}

@Composable
private fun ConnectionCard(snap: EngineSnapshot, onDisconnect: () -> Unit) {
    val extra = LocalExtra.current
    val (color, title, detail) = when {
        snap.error != null -> Triple(MaterialTheme.colorScheme.error, "Connection problem", snap.error)
        snap.connected -> Triple(extra.good, snap.pcName ?: snap.host, "Connected · ${snap.host}")
        else -> Triple(extra.warn, "Connecting…", "Trying to reach ${snap.host}")
    }
    SectionTitle("Connection")
    AppCard {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Box(Modifier.size(38.dp).clip(CircleShape).background(color.copy(alpha = 0.15f)), contentAlignment = Alignment.Center) {
                Box(Modifier.size(10.dp).clip(CircleShape).background(color))
            }
            Spacer(Modifier.width(14.dp))
            Column(Modifier.weight(1f)) {
                Text(title, style = MaterialTheme.typography.titleMedium, maxLines = 1, overflow = TextOverflow.Ellipsis)
                Text(detail, style = Caption, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
        }
        if (snap.connected) {
            Text(
                "Latency ≈ ${snap.latencyMs} ms · Lost ${snap.lost} · Underruns ${snap.underruns}",
                style = Caption, color = MaterialTheme.colorScheme.onSurfaceVariant,
                modifier = Modifier.padding(start = 52.dp, top = 6.dp),
            )
        }
        Spacer(Modifier.height(16.dp))
        OutlinedButton(onClick = onDisconnect, modifier = Modifier.fillMaxWidth()) {
            Text("Disconnect")
        }
    }
}

// ---- Building blocks ----

@Composable
private fun SectionTitle(text: String) {
    Text(
        text.uppercase(java.util.Locale.ROOT),
        style = MaterialTheme.typography.labelMedium,
        color = MaterialTheme.colorScheme.onSurfaceVariant,
        modifier = Modifier.padding(start = 4.dp, top = 8.dp),
    )
}

@Composable
private fun AppCard(contentPadding: androidx.compose.ui.unit.Dp = 18.dp, content: @Composable ColumnScope.() -> Unit) {
    Card(
        shape = RoundedCornerShape(16.dp),
        colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surface),
        border = BorderStroke(1.dp, MaterialTheme.colorScheme.outlineVariant),
        modifier = Modifier.fillMaxWidth(),
    ) {
        Column(Modifier.padding(contentPadding), content = content)
    }
}

@Composable
private fun IconBadge(icon: Int) {
    Box(
        Modifier.size(38.dp).clip(RoundedCornerShape(10.dp)).background(LocalExtra.current.accentSoft),
        contentAlignment = Alignment.Center,
    ) {
        Icon(painterResource(icon), contentDescription = null, tint = MaterialTheme.colorScheme.primary, modifier = Modifier.size(20.dp))
    }
}

@Composable
private fun SwitchRow(
    icon: Int?,
    title: String,
    subtitle: String,
    checked: Boolean,
    enabled: Boolean = true,
    onChange: (Boolean) -> Unit,
) {
    Row(
        Modifier.fillMaxWidth().clickable(enabled = enabled) { onChange(!checked) },
        verticalAlignment = Alignment.CenterVertically,
    ) {
        if (icon != null) {
            IconBadge(icon)
            Spacer(Modifier.width(14.dp))
        }
        Column(Modifier.weight(1f).padding(end = 12.dp)) {
            val alpha = if (enabled) 1f else 0.45f
            Text(title, style = MaterialTheme.typography.bodyLarge, color = MaterialTheme.colorScheme.onSurface.copy(alpha = alpha))
            Text(subtitle, style = Caption, color = MaterialTheme.colorScheme.onSurfaceVariant.copy(alpha = alpha))
        }
        Switch(checked = checked, onCheckedChange = onChange, enabled = enabled)
    }
}

@Composable
private fun LevelBar(level: Float, modifier: Modifier = Modifier) {
    Box(modifier.fillMaxWidth().height(4.dp).clip(RoundedCornerShape(2.dp)).background(LocalExtra.current.track)) {
        Box(Modifier.fillMaxWidth(level).height(4.dp).background(MaterialTheme.colorScheme.primary))
    }
}

@Composable
private fun InputPicker(options: List<InputOption>, selectedId: Int, enabled: Boolean, onSelect: (Int) -> Unit) {
    var open by remember { mutableStateOf(false) }
    val selected = options.firstOrNull { it.id == selectedId } ?: options.first()
    val alpha = if (enabled) 1f else 0.45f
    Text("Microphone source", style = MaterialTheme.typography.bodyLarge, color = MaterialTheme.colorScheme.onSurface.copy(alpha = alpha))
    Spacer(Modifier.height(8.dp))
    Box {
        Surface(
            shape = RoundedCornerShape(10.dp),
            border = BorderStroke(1.dp, MaterialTheme.colorScheme.outlineVariant),
            color = MaterialTheme.colorScheme.surface,
            modifier = Modifier.fillMaxWidth().clickable(enabled = enabled) { open = true },
        ) {
            Row(Modifier.padding(horizontal = 14.dp, vertical = 12.dp), verticalAlignment = Alignment.CenterVertically) {
                Text(
                    selected.label, style = MaterialTheme.typography.bodyMedium, maxLines = 1, overflow = TextOverflow.Ellipsis,
                    color = MaterialTheme.colorScheme.onSurface.copy(alpha = alpha), modifier = Modifier.weight(1f),
                )
                Text("▾", color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
        }
        DropdownMenu(expanded = open, onDismissRequest = { open = false }) {
            options.forEach { o ->
                DropdownMenuItem(
                    text = { Text(o.label, color = if (o.id == selectedId) MaterialTheme.colorScheme.primary else Color.Unspecified) },
                    onClick = { open = false; onSelect(o.id) },
                )
            }
        }
    }
}

@Composable
private fun ManualAddressDialog(initial: String, onDismiss: () -> Unit, onConnect: (String) -> Unit) {
    var text by remember { mutableStateOf(initial) }
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text("Enter address manually") },
        text = {
            Column {
                Text(
                    "Enter the IP address shown at the bottom of the SoundSwap window on your PC.",
                    style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
                Spacer(Modifier.height(14.dp))
                OutlinedTextField(
                    value = text,
                    onValueChange = { text = it.trim() },
                    placeholder = { Text("e.g. 192.168.1.20") },
                    singleLine = true,
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri),
                    modifier = Modifier.fillMaxWidth(),
                )
            }
        },
        confirmButton = {
            TextButton(onClick = { onConnect(text) }, enabled = text.isNotBlank()) { Text("Connect") }
        },
        dismissButton = { TextButton(onClick = onDismiss) { Text("Cancel") } },
    )
}

/** Microphone sources; updated as headsets are plugged in and removed. */
@Composable
private fun rememberInputOptions(): List<InputOption> {
    val context = LocalContext.current
    val am = remember { context.getSystemService(AudioManager::class.java) }
    var options by remember { mutableStateOf(listInputs(am)) }
    DisposableEffect(am) {
        val cb = object : AudioDeviceCallback() {
            override fun onAudioDevicesAdded(addedDevices: Array<out AudioDeviceInfo>) { options = listInputs(am) }
            override fun onAudioDevicesRemoved(removedDevices: Array<out AudioDeviceInfo>) { options = listInputs(am) }
        }
        am.registerAudioDeviceCallback(cb, null)
        onDispose { am.unregisterAudioDeviceCallback(cb) }
    }
    return options
}

private fun listInputs(am: AudioManager): List<InputOption> {
    val list = mutableListOf(InputOption(0, "Automatic (headset mic if one is connected)"))
    am.getDevices(AudioManager.GET_DEVICES_INPUTS).forEach { d ->
        val kind = when (d.type) {
            AudioDeviceInfo.TYPE_BUILTIN_MIC -> "Phone microphone"
            AudioDeviceInfo.TYPE_WIRED_HEADSET -> "Wired headset"
            AudioDeviceInfo.TYPE_USB_HEADSET, AudioDeviceInfo.TYPE_USB_DEVICE -> "USB"
            AudioDeviceInfo.TYPE_BLUETOOTH_SCO, 26 /* TYPE_BLE_HEADSET */ -> "Bluetooth headset"
            else -> return@forEach
        }
        val addr = if (d.type == AudioDeviceInfo.TYPE_BUILTIN_MIC && d.address.isNotEmpty()) " (${d.address})" else ""
        list += InputOption(d.id, "$kind$addr")
    }
    return list
}
