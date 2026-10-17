package com.soundswap.app.ui

import androidx.compose.foundation.Canvas
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.foundation.layout.size
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Typography
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.Immutable
import androidx.compose.runtime.staticCompositionLocalOf
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.sp

/** Windows uygulamasıyla aynı palet. */
@Immutable
data class Extra(val good: Color, val warn: Color, val accentSoft: Color, val track: Color)

val LocalExtra = staticCompositionLocalOf { lightExtra }

private val Brand = Color(0xFF2563EB)

private val lightScheme = lightColorScheme(
    primary = Brand,
    onPrimary = Color.White,
    background = Color(0xFFF3F4F6),
    onBackground = Color(0xFF111827),
    surface = Color.White,
    onSurface = Color(0xFF111827),
    surfaceVariant = Color(0xFFF3F4F6),
    onSurfaceVariant = Color(0xFF6B7280),
    surfaceTint = Color.White,
    surfaceContainerLowest = Color.White,
    surfaceContainerLow = Color.White,
    surfaceContainer = Color.White,
    surfaceContainerHigh = Color.White,
    surfaceContainerHighest = Color.White,
    primaryContainer = Color(0xFFEFF4FF),
    onPrimaryContainer = Brand,
    secondary = Brand,
    secondaryContainer = Color(0xFFEFF4FF),
    onSecondaryContainer = Brand,
    outline = Color(0xFF9CA3AF),
    outlineVariant = Color(0xFFE5E7EB),
    error = Color(0xFFDC2626),
)

private val darkScheme = darkColorScheme(
    primary = Color(0xFF3B82F6),
    onPrimary = Color.White,
    background = Color(0xFF141518),
    onBackground = Color(0xFFF3F4F6),
    surface = Color(0xFF1C1D22),
    onSurface = Color(0xFFF3F4F6),
    surfaceVariant = Color(0xFF22242A),
    onSurfaceVariant = Color(0xFF9CA3AF),
    surfaceTint = Color(0xFF1C1D22),
    surfaceContainerLowest = Color(0xFF1C1D22),
    surfaceContainerLow = Color(0xFF1C1D22),
    surfaceContainer = Color(0xFF22242A),
    surfaceContainerHigh = Color(0xFF22242A),
    surfaceContainerHighest = Color(0xFF22242A),
    primaryContainer = Color(0xFF1E2A44),
    onPrimaryContainer = Color(0xFF93B4FD),
    secondary = Color(0xFF3B82F6),
    secondaryContainer = Color(0xFF1E2A44),
    onSecondaryContainer = Color(0xFF93B4FD),
    outline = Color(0xFF6B7280),
    outlineVariant = Color(0xFF2B2D33),
    error = Color(0xFFEF4444),
)

private val lightExtra = Extra(Color(0xFF16A34A), Color(0xFFD97706), Color(0xFFEFF4FF), Color(0xFFE5E7EB))
private val darkExtra = Extra(Color(0xFF22C55E), Color(0xFFF59E0B), Color(0xFF1E2A44), Color(0xFF2B2D33))

private val typography = Typography().let {
    it.copy(
        titleLarge = it.titleLarge.copy(fontWeight = FontWeight.SemiBold, fontSize = 22.sp),
        titleMedium = it.titleMedium.copy(fontWeight = FontWeight.SemiBold),
        labelLarge = it.labelLarge.copy(fontWeight = FontWeight.SemiBold),
    )
}

@Composable
fun SoundSwapTheme(content: @Composable () -> Unit) {
    val dark = isSystemInDarkTheme()
    androidx.compose.runtime.CompositionLocalProvider(LocalExtra provides if (dark) darkExtra else lightExtra) {
        MaterialTheme(colorScheme = if (dark) darkScheme else lightScheme, typography = typography, content = content)
    }
}

/** SoundSwap logosu — assets/logo.svg ile aynı geometri. */
@Composable
fun Logo(size: Dp) {
    Canvas(Modifier.size(size)) {
        val k = this.size.minDimension / 32f
        drawCircle(Brand, radius = 15f * k, center = Offset(16f * k, 16f * k))
        val bars = listOf(
            Triple(8f, 12f, 20f), Triple(12f, 8f, 24f), Triple(16f, 10f, 22f),
            Triple(20f, 7f, 25f), Triple(24f, 12f, 20f),
        )
        for ((x, y1, y2) in bars) {
            drawLine(Color.White, Offset(x * k, y1 * k), Offset(x * k, y2 * k), strokeWidth = 2.6f * k, cap = StrokeCap.Round)
        }
    }
}

val Caption = TextStyle(fontSize = 12.sp, lineHeight = 16.sp)
