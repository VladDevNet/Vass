package com.vass.overlay

import androidx.core.content.FileProvider

// A concrete provider avoids device-specific issues seen with declaring the
// AndroidX FileProvider class directly in an app manifest.
class VassUpdateFileProvider : FileProvider()
