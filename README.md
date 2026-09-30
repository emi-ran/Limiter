# Limiter

[![Windows CI](https://github.com/emi-ran/Limiter/actions/workflows/windows.yml/badge.svg)](https://github.com/emi-ran/Limiter/actions/workflows/windows.yml)

Windows için uygulama ve process (PID) bazında ağ trafiği izleme, hız sınırlama ve engelleme prototipi. C# / .NET 8, WPF ve WinDivert ile geliştirilir.

## Çalıştırma

1. [Releases](https://github.com/emi-ran/Limiter/releases) bölümündeki Windows x64 ZIP paketini bir klasöre çıkarın. Varsa açık Limiter penceresini kapatıp `Limiter.exe` dosyasını açın. Windows yönetici izni isteyecek; WinDivert sürücüsü için gerekli.
2. Limitlemek istediğiniz uygulamada ağ trafiği başlatın. Uygulama listede görünecek.
3. Uygulamayı seçip indirme ve yükleme sınırlarını **KB/sn** olarak girin. `0` sınırsız, en küçük pozitif sınır `16 KB/sn` anlamına gelir. **Uygula** ile kaydedin.

Windows x64 ve [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) gerekir. Kaynak koddan derlemek için .NET 8 SDK kurun ve `dotnet build Limiter\Limiter.csproj -c Release` kullanın.

GitHub'dan klonladıktan sonra çalıştırılabilir sürüm oluşturmak için:

```powershell
dotnet publish Limiter/Limiter.csproj -c Release -r win-x64 --self-contained false -o dist
```

`dist\Limiter.exe` dosyasını açın; yanındaki `WinDivert.dll` ve `WinDivert64.sys` dosyalarını taşırken birlikte tutun. Releases paketleri etiketlenmiş sürümlerdir; en güncel commit için [Actions](https://github.com/emi-ran/Limiter/actions) çalışmasının `Limiter-win-x64` artifact paketini kullanabilirsiniz.

## Şu anki kapsam

- NetLimiter benzeri üst araç çubuğu, Activity / Rule List görünümü ve yeniden boyutlandırılabilir Info View paneli
- **Limiter On** ile uygulama ve PID sınırlarını birlikte duraklatma; trafik izleme devam eder, kayıtlı kurallar korunur. Yeniden açıldığında kurallar uygulanır. Bu anahtar oturumluk olup uygulama her açılışta etkin başlar. **Blocker On** aynı şekilde engelleme kurallarını topluca duraklatır. Priorities henüz kullanıma açık değildir.
- Canlı uygulama trafiği ve toplam bayt sayısı
- Kompakt koyu arayüz, uygulama araması, toplam hızlar ve seçili uygulama ayrıntıları
- Dosyadan arka planda okunup önbelleğe alınan uygulama simgeleri
- Uygulama satırını okla açıp PID bazında trafik gösterimi. Aynı dosya yolundaki process'ler gruplanır. Uygulama satırındaki sınır toplam trafiğe, alt PID satırındaki sınır yalnızca o process'e uygulanır. İki sınır birlikte uygulanabilir. PID kuralları kalıcı kaydedilmez, process kapanınca veya Limiter kapatılınca sona erer. Yalnızca ağ bağlantısı eşleşen process'ler listelenir; izleme sırasında kapanan process'lerin toplam kullanımı oturum sonuna kadar korunur.
- Uygulama ve PID başına ayrı indirme/yükleme sınırı; yön anahtarları hız değerini silmeden hemen açıp kapatır. Yeni hız değerleri Uygula ile kaydedilir.
- Uygulama ve PID başına ayrı indirme/yükleme engelleme. Engelleme hız sınırından önce uygulanır ve ilgili bekleyen paketler kuyruktan çıkarılır. Uygulama engeli alt PID'lere de uygulanır.
- Sınırları kaldır düğmesi yalnızca hız sınırlarını temizler; engeller yön anahtarlarından kaldırılır. Eski kayıtların hız sınırları varsayılan olarak etkin yüklenir.
- `%LOCALAPPDATA%\Limiter\rules.json` içinde kalıcı kurallar
- TCP, UDP, IPv4 ve temel IPv6 paketleri
- Tekrar gelen ve hâlâ kuyrukta bekleyen TCP veri paketlerini ayıklama
- Sınırsız trafikte paket kopyalamadan geçiş
- Sınır değiştirildiğinde bekleyen paketleri yeni hıza göre yeniden zamanlama
- Tablo başlıklarına tıklayarak artan/azalan sıralama; aktif sütun mavi vurgu ve yön okuyla, seçilen kriter tablonun üstünde açık metinle gösterilir. Kurallar sütunu kayıtlı indirme sınırına göre sıralar; hızlar her yenilemede bir kez sıralanır ve başlangıçta indirme hızı en yüksek uygulama üstte gösterilir

Bu ilk sürüm yalnızca açıkken çalışır. Mevcut TCP bağlantıları Windows bağlantı tablosundan alınır; önce kurulmuş UDP bağlantıları süreçle eşleşmeyebilir. IPv6 uzantı başlıkları ve parçalanmış paketler limit ve engelleme hesabına girmez. Engelleme yalnızca motorun süreçle eşleştirdiği TCP/UDP trafiğini kapsar; loopback trafiği yakalanmaz. Sınırlı bir bağlantının kuyruğu dolduğunda paketler serbest bırakılmaz: TCP bu paketleri yeniden ister, UDP paketleri kaybolabilir. Bu nedenle özellikle çok düşük sınırlar ve çoklu bağlantılarda aktarım dalgalanabilir. Ayrı Windows servisi ve daha sıkı paket işleme sonraki aşamadır.

Listede gösterilen hız, başarıyla iletilen ağ paketlerinin gerçek süreye göre hesaplanan yaklaşık 3 saniyelik ortalamasıdır. Ağ başlıkları ve yeniden gönderilen paketler bu sayıya dahildir; IDM gibi uygulamaların dosya aktarım hızından biraz farklı olabilir. `—` işareti, o uygulama için Limiter içinde bir hız sınırı veya engelleme kuralı kaydedilmediğini belirtir.

Çekirdek kontrollerini çalıştırmak için `dotnet run --project Limiter.EngineChecks\Limiter.EngineChecks.csproj -c Release` kullanın. Bu kontroller paket ayrıştırmayı, gerçek Windows TCP bağlantı eşleştirmesini, zamanlama hesabını ve ayrıştırma maliyetini doğrular. Canlı UDP engelleme ve geri dönüş kontrolü için uygulamayı kapatıp yönetici terminalinde `dotnet run --project Limiter.EngineChecks\Limiter.EngineChecks.csproj -c Release -- --live-blocker` çalıştırın. Bu isteğe bağlı kontrol yalnızca kendi test process'ini engeller, kuralları geçici dosyaya yazar ve 1.1.1.1 DNS sunucusuna example.com sorguları gönderir. Gerçek TCP yükü, IPv6 ve uzun süreli aktarım sonuçları ayrıca test edilmelidir.

## CI / CD

[Windows CI](https://github.com/emi-ran/Limiter/actions/workflows/windows.yml), `main` push'larında, pull request'lerde ve elle başlatıldığında Windows runner üzerinde:

1. .NET 8 SDK ile projeleri restore eder ve Release derlemesi yapar.
2. `Limiter.EngineChecks` kontrollerini çalıştırır. Bu aşama WinDivert yakalama sürücüsünü başlatmaz; canlı engelleme testi CI'da çalıştırılmaz.
3. Windows x64 için framework-dependent paket oluşturur ve 14 gün saklanan `Limiter-win-x64` artifact'ını yükler.

`v*` biçimindeki bir sürüm etiketi push edildiğinde aynı kontrollerden sonra ZIP dosyası ve otomatik sürüm notlarıyla GitHub Release yayınlanır. Release yayınlama yetkisi yalnızca bu ayrı job'a verilir. Etiketleme örneği:

```powershell
git tag v0.14
git push origin v0.14
```

Etiket adını yeni sürüm numarasıyla değiştirin. Canlı TCP/UDP, IPv6, gerçek masaüstü etkileşimi ve uzun süreli yük davranışları otomatik CI kapsamının dışındadır.

## Proje yapısı

- `Limiter/`: WPF arayüzü, paket eşleştirme, kuyruk/zamanlama ve kural saklama.
- `Limiter.EngineChecks/`: çekirdek doğrulamalar ve isteğe bağlı canlı UDP engelleme testi.
- `vendor/WinDivert-2.2.2-A/`: WinDivert yerel bileşenleri ve lisansı.
- `.github/workflows/windows.yml`: Windows derleme, kontroller, paketleme ve etiketli sürüm yayınlama.

## Üçüncü taraf bileşen

WinDivert 2.2.2 (`vendor/WinDivert-2.2.2-A`) kullanılır. WinDivert LGPLv3 veya GPLv2 seçenekleriyle dağıtılır; tam lisans metni `vendor/WinDivert-2.2.2-A/LICENSE` dosyasındadır. Projenin kendi kodu için lisans henüz seçilmemiştir.
