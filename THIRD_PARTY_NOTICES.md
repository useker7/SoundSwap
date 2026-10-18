# Third-party notices

SoundSwap itself is proprietary (see [LICENSE](LICENSE)). It uses the following third-party
components, which remain under their own licenses.

## Windows app

### NAudio 2.2.1 (NAudio.Core, NAudio.Wasapi)

<https://github.com/naudio/NAudio>, MIT License

```
Copyright 2020 Mark Heath

Permission is hereby granted, free of charge, to any person obtaining a copy of this software
and associated documentation files (the "Software"), to deal in the Software without
restriction, including without limitation the rights to use, copy, modify, merge, publish,
distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the
Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or
substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING
BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM,
DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
```

### VB-Audio Virtual Cable (VB-CABLE)

<https://vb-cable.com>, © VB-Audio Software (Vincent Burel)

VB-CABLE is **not** included in SoundSwap. When the user chooses to install the virtual
microphone, SoundSwap downloads the official driver pack from vb-audio.com and runs its
installer. VB-CABLE is donationware: free to use, with payment expected if you find it useful
or use it professionally. Its device name stays identifiable as "VB-Audio Virtual Cable", as its
license requires.

## Android app

### AndroidX (Activity, Compose UI, Foundation, Material 3) and Kotlin standard library

<https://developer.android.com/jetpack/androidx>, <https://kotlinlang.org>

Copyright The Android Open Source Project and JetBrains s.r.o. and Kotlin contributors.
Licensed under the Apache License, Version 2.0: <https://www.apache.org/licenses/LICENSE-2.0>

### Material Icons (speaker, microphone, desktop computer)

<https://github.com/google/material-design-icons>, Copyright Google LLC.
Licensed under the Apache License, Version 2.0: <https://www.apache.org/licenses/LICENSE-2.0>

## iOS app

Uses only Apple system frameworks (SwiftUI, AVFoundation, Network). No third-party code.
