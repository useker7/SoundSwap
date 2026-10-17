# SoundSwap

<p><a href="README.md">English</a> · <b>Türkçe</b></p>

Bilgisayarın sesini telefona aktarır, telefonun mikrofonunu da bilgisayara mikrofon olarak verir.

<p align="center">
  <img src="docs/screenshots/windows-light.png" width="340" alt="SoundSwap Windows, açık tema">
  &nbsp;
  <img src="docs/screenshots/windows-dark.png" width="340" alt="SoundSwap Windows, koyu tema">
</p>

> Uygulamaların arayüz dili İngilizcedir; aşağıda düğme ve seçenek adları uygulamada göründüğü gibi verilmiştir.

- **PC → Telefon:** Windows'ta çalan her şey (oyun, müzik, Discord…) telefonun hoparlöründen ya da telefona takılı kulaklıktan çıkar.
- **Telefon → PC:** Telefonun (veya telefona takılı kulaklığın) mikrofonu Windows'ta normal bir mikrofon gibi görünür.
- **Düşük gecikme:** Sıkıştırmasız 48 kHz PCM, 5 ms'lik UDP paketleri, ayarlanabilir tampon. Wi-Fi'da PC → telefon tipik toplam gecikme **~50–100 ms** (en büyük pay telefonun ses donanımında).

```
 Windows (SoundSwap.exe)                               Telefon (Android / iOS)
 ┌──────────────────────────┐   UDP 50505, PCM 48k    ┌────────────────────────┐
 │ WASAPI loopback (çalan   │ ── stereo, 5 ms ──────► │ jitter tampon → hoparlör│
 │ ses) ─► 5 ms paketler    │                         │ / kulaklık              │
 │                          │ ◄── mono, 5 ms ──────── │ mikrofon / kulaklık mik.│
 │ jitter tampon ─► sanal   │                         └────────────────────────┘
 │ kablo ─► "SoundSwap      │
 │ Microphone"              │
 └──────────────────────────┘
```

## Klasörler

| Klasör | İçerik |
|---|---|
| `windows/SoundSwap` | Windows uygulaması (C#, .NET 8, WPF, NAudio) |
| `windows/SoundSwap.Tests` | Jitter tamponu ve protokol için xUnit testleri |
| `android` | Android uygulaması (Kotlin, Jetpack Compose, Android 8+, JVM birim testleriyle) |
| `ios` | iOS uygulaması (Swift/SwiftUI, iOS 15+, XcodeGen projesi) |
| `.github/workflows` | Üç platform için CI ve sürüm (release) iş akışları. `ios.yml`, Mac'iniz yoksa iOS uygulamasını GitHub'da derler |
| `dist` | `build.ps1` çıktısı (git'e eklenmez; hazır dosyalar [Releases](../../releases/latest) sayfasındadır) |
| `build.ps1` | Windows + Android'i derleyip `dist` klasörüne koyar |

---

## Kurulum

### 1. Windows

1. [Releases](../../releases/latest) sayfasından `SoundSwap.exe`'yi indirip çalıştırın. [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) gerekir.
2. **Sanal mikrofon:** Telefon mikrofonunun diğer programlarda görünmesi için gerekli. Bilgisayarda yoksa uygulamada **Install virtual microphone** düğmesi çıkar. Tek tıkla [VB-Audio Virtual Cable](https://vb-cable.com) resmi sitesinden indirilir. Dosya, yönetici izniyle çalıştırılmadan önce VB-Audio'nun dijital imzasıyla doğrulanır, sonra sessizce kurulur.
   - Windows bir yönetici izni (UAC) ve bir sürücü onayı ister. Bazı bilgisayarlarda kurulumdan sonra yeniden başlatma gerekebilir.
   - Aygıtın adı otomatik olarak **SoundSwap Microphone** yapılır (oynatma ucu: *SoundSwap Microphone Feed*). VB-Cable zaten kuruluysa uygulamada **Rename to "SoundSwap Microphone"** bağlantısı çıkar; bu işlem yönetici izni istemez.
   - VB-Cable, VB-Audio Software'in bağış usulü (donationware) ürünüdür. Lisans koşulu gereği Windows'ta aygıt adının sonunda "(VB-Audio Virtual Cable)" görünmeye devam eder. Beğenirseniz [vb-cable.com](https://vb-cable.com) üzerinden destek olabilirsiniz.
   - Daha az gecikme için (isteğe bağlı): `VBCABLE_ControlPanel.exe` → *Options* → **Max Latency** değerini 2048 veya 1024 yapın.
3. Windows Güvenlik Duvarı sorarsa **Özel ağlar** için izin verin. Sormazsa yönetici PowerShell'de şunu çalıştırın:
   ```bash
   netsh advfirewall firewall add rule name="SoundSwap" dir=in action=allow protocol=UDP localport=50505
   ```
4. Pencerede:
   - **Send PC audio to phone:** Windows'un o an seçili (varsayılan) çıkışında çalan ses gönderilir. Windows'ta hoparlörü değiştirirseniz uygulama kendiliğinden yeni cihaza geçer.
   - **Use phone microphone:** Sanal mikrofon otomatik bulunur. Discord, Zoom, OBS vb. programlarda mikrofon olarak **SoundSwap Microphone**'u seçin.
   - **Mute PC speakers while a phone is connected:** Ses yalnızca telefondan gelsin istiyorsanız açın. Test edildi: Windows'ta sessize almak aktarımı kesmez, ama **ses seviyesini kısmak aktarılan sesi de kısar**. Bu yüzden PC sesini kısmak yerine bu seçeneği kullanın, sesi telefondan ayarlayın.
   - **Start with Windows:** Oturum açınca sistem tepsisinde sessizce çalışır.
   - Pencereyi kapatınca uygulama sistem tepsisinde çalışmaya devam eder. Tamamen kapatmak için tepsi simgesine sağ tıklayıp **Exit**'i seçin.
   - Bilgisayar yerel ağda kendini Bonjour/mDNS ile (`_soundswap._udp`) duyurur. Telefon uygulamaları onu IP yazmadan bulur. Pencerenin altındaki ad ve IP, elle bağlanmak gerekirse diye gösterilir.

### 2. Android

1. [Releases](../../releases/latest) sayfasındaki `SoundSwap.apk`'yi telefona atıp açın ("bilinmeyen kaynaklar" izni isteyecek) ya da USB hata ayıklama açıksa:
   ```bash
   adb install -r SoundSwap.apk
   ```
2. Uygulamayı açın. Aynı ağdaki bilgisayarlar **kendiliğinden listelenir**; bilgisayara dokunun. Gerekirse **Enter address manually** ile PC penceresinin altında yazan IP'yi girebilirsiniz.
3. Ekran kapalıyken de çalışır (bildirimde **Stop** düğmesi var).

### 3. iOS (iPhone / iPad)

iOS uygulamaları yalnızca macOS'ta derlenebilir. İki yol var:

**A) Mac + Xcode varsa**
```bash
brew install xcodegen
cd ios && xcodegen generate && open SoundSwap.xcodeproj
```
Xcode'da *Signing & Capabilities* → Team olarak Apple kimliğinizi seçin, iPhone'u bağlayın, ▶ ile çalıştırın.

**B) Mac yoksa (yalnızca Windows)**
1. `SoundSwap-unsigned.ipa`'yı [Releases](../../releases/latest) sayfasından ya da *Actions* sekmesindeki **iOS** iş akışının çıktısından indirin (iş akışını elle de başlatabilirsiniz).
2. Windows'ta [Sideloadly](https://sideloadly.io/) ile bu IPA'yı kendi Apple kimliğinizle imzalayıp USB'den iPhone'a yükleyin.
3. Ücretsiz Apple hesabında imza 7 günde bir yenilenmelidir (Sideloadly ile tekrar yükleyin). Ücretli geliştirici hesabında 1 yıl geçerlidir.

Uygulamayı açın. İlk açılışta "yerel ağ" iznine **İzin Ver** deyin (bilgisayarları bulmak için gerekli), sonra listede çıkan bilgisayara dokunun. Mikrofon izni ilk bağlantıda istenir. Gerekirse **Enter address manually** ile IP yazılabilir.

> Projeyi güncelledikten sonra Mac'te `cd ios && xcodegen generate` komutunu tekrar çalıştırın (uygulama simgesi ve yeni dosyalar projeye eklenir).

---

## Gecikmeyi en aza indirmek için

| Ne | Neden |
|---|---|
| **5 GHz Wi-Fi** (veya PC kabloyla modeme bağlı) | 2.4 GHz'de dalgalanma (jitter) çok daha fazla |
| **USB bağlantı paylaşımı** (Android: *USB tethering*, iPhone: USB ile *Kişisel Erişim Noktası*) | Kablosuz gecikme tamamen ortadan kalkar. PC penceresinde yeni bir IP görünür (ör. `192.168.42.x` / `172.20.10.x`), telefona onu girin |
| **Kablolu kulaklık** | Bluetooth kulaklıklar kendileri 100–250 ms ekler |
| **Ses tamponu** ayarı (telefonda ve PC'de) | Düşük = az gecikme. Cızırtı/kesilme olursa 5–10 ms artırın. Wi-Fi'da genelde 20–40 ms iyi çalışır |
| VB-Cable *Max Latency* | Yukarıdaki kurulum adımına bakın |

Ekranda gösterilen istatistikler ("Latency ≈ … ms", "Lost", "Underruns") ayarı yaparken yol gösterir. **Underruns** (boşalma) sayısı sürekli artıyorsa tamponu büyütün.

### Kulaklık / mikrofon seçenekleri

- **Kablolu kulaklık (mikrofonlu):** Android ve iOS otomatik olarak kulaklığın mikrofonunu kullanır.
- **Bluetooth kulaklık:** Ses A2DP ile yüksek kalitede gider. Mikrofon için:
  - Android: *Microphone source* listesinden *Bluetooth headset*'i seçin.
  - iOS: *Bluetooth headset microphone* seçeneğini açın.
  - Bluetooth mikrofon açılınca kulaklık "telefon görüşmesi" moduna (HFP) geçer. Bu modda hem ses hem mikrofon kalitesi düşer. Bu bir Bluetooth kısıtlamasıdır.
- **Hoparlörden dinleyip telefon mikrofonuyla konuşuyorsanız** **Echo cancellation**'ı açın. Kapalıyken PC'deki karşı taraf kendi sesini yankı olarak duyar.

---

## Kaynaktan derleme

```bash
powershell -ExecutionPolicy Bypass -File build.ps1
```
- Windows: .NET 8 SDK gerekir. `dotnet` PATH'te değilse script `%LOCALAPPDATA%\Microsoft\dotnet-sdk` altındaki kullanıcı kurulumunu da arar.
- Android: Android Studio ile `android` klasörünü açabilir ya da `android\gradlew.bat assembleDebug` çalıştırabilirsiniz (JDK olarak Android Studio'nun `jbr`'si kullanılır).
- Testler: `dotnet test windows/SoundSwap.Tests` ve `android\gradlew.bat testReleaseUnitTest`.
- Release APK, kişisel kullanım için debug anahtarıyla imzalanır.

## Protokol

Tüm sayılar little-endian. Port: **UDP 50505** (PC dinler). Ses: **PCM s16le, 48 000 Hz**.

Her paketin 8 baytlık başlığı:

| Bayt | Alan |
|---|---|
| 0–1 | `'S' 'W'` |
| 2 | sürüm = 1 |
| 3 | tip |
| 4–7 | sıra no (uint32) |

| Tip | Yön | Yük |
|---|---|---|
| 1 `HELLO` | telefon → PC, 500 ms'de bir | `[bayraklar][cihaz adı UTF-8]`. Bayrak 1 = PC sesini istiyor, 2 = mikrofon gönderiyor |
| 2 `HELLO_ACK` | PC → telefon | PC adı (UTF-8) |
| 3 `PC_AUDIO` | PC → telefon | stereo, 240 kare (5 ms) = 960 bayt |
| 4 `MIC_AUDIO` | telefon → PC | mono, 240 örnek (5 ms) = 480 bayt (PC her uzunluğu kabul eder) |
| 5 `BYE` | telefon → PC | – |
| 6 `DISCOVER` | telefon → yayın (broadcast) | – |
| 7 `DISCOVER_REPLY` | PC → telefon | PC adı (UTF-8) |

- **Bulma:** PC kendini DNS-SD/mDNS ile `<bilgisayar adı>._soundswap._udp.local` olarak duyurur (SRV: port 50505, TXT: `v=1`). Ayrıca `DISCOVER` yayın paketlerine de yanıt verir.
- PC, son `HELLO` gönderen telefonu istemci kabul eder (aynı anda tek telefon). 3 sn `HELLO` gelmezse bağlantı düşer.
- Alıcılar, sıra numarasına göre geç kalmış veya yinelenen paketleri atar.
- **Jitter tamponu** (üç platformda aynı algoritma): Önce hedef doluluğa kadar doldurur, sonra çalar. Boşalırsa sessizlik verip yeniden doldurur. İki cihazın saatleri arasındaki küçük farkı (drift) dengelemek için, son 0,5 sn'deki en düşük doluluk hedefi 8 ms'den fazla aşarsa fazlalığı atar. Böylece gecikme zamanla büyümez.

## Sınırlamalar

- Aynı anda tek telefon bağlanabilir.
- Trafik şifresizdir ve yalnızca yerel ağ / USB bağlantısı için tasarlanmıştır. İnternet üzerinden açmayın.
- Bant genişliği PC → telefon yönünde ~1,6 Mbit/s, ters yönde ~0,8 Mbit/s (sıkıştırma yok, çünkü sıkıştırma gecikme ekler).

## Lisans

[MIT](LICENSE). VB-CABLE bu deponun parçası değildir; VB-Audio Software'in bağış usulü (donationware) ürünüdür ve kendi lisansına tabidir.
