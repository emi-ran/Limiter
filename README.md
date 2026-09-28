# Limiter

Windows için uygulama bazında indirme ve yükleme hız sınırı prototipi.

## Çalıştırma

1. Varsa açık Limiter penceresini kapatıp `dist-v12\Limiter.exe` dosyasını açın. Windows yönetici izni isteyecek; WinDivert sürücüsü için gerekli.
2. Limitlemek istediğiniz uygulamada ağ trafiği başlatın. Uygulama listede görünecek.
3. Uygulamayı seçip indirme ve yükleme sınırlarını **KB/sn** olarak girin. `0` sınırsız anlamına gelir.

Kaynak koddan derlemek için `dotnet build Limiter\Limiter.csproj -c Release` kullanın. .NET 8 Desktop Runtime gerekir.

GitHub'dan klonladıktan sonra çalıştırılabilir sürüm oluşturmak için:

```powershell
dotnet publish Limiter/Limiter.csproj -c Release -r win-x64 --self-contained false -o dist-v12
```

Hazır Windows x64 paketi özel deponun **Releases** bölümünde bulunur.

## Şu anki kapsam

- Canlı uygulama trafiği ve toplam bayt sayısı
- Kompakt koyu arayüz, uygulama araması, toplam hızlar ve seçili uygulama ayrıntıları
- Dosyadan arka planda okunup önbelleğe alınan uygulama simgeleri
- Uygulama satırını okla açıp PID bazında trafik gösterimi. Aynı dosya yolundaki process'ler gruplanır. Uygulama satırındaki sınır toplam trafiğe, alt PID satırındaki sınır yalnızca o process'e uygulanır. İki sınır birlikte uygulanabilir. PID kuralları kalıcı kaydedilmez, process kapanınca veya Limiter kapatılınca sona erer. Yalnızca ağ bağlantısı eşleşen process'ler listelenir; izleme sırasında kapanan process'lerin toplam kullanımı oturum sonuna kadar korunur.
- Uygulama başına ayrı indirme ve yükleme sınırı
- `%LOCALAPPDATA%\Limiter\rules.json` içinde kalıcı kurallar
- TCP, UDP, IPv4 ve temel IPv6 paketleri
- Tekrar gelen ve hâlâ kuyrukta bekleyen TCP veri paketlerini ayıklama
- Sınırsız trafikte paket kopyalamadan geçiş
- Sınır değiştirildiğinde bekleyen paketleri yeni hıza göre yeniden zamanlama
- Tablo başlıklarıyla sayısal sıralama; hızlar her yenilemede bir kez sıralanır ve başlangıçta indirme hızı en yüksek uygulama üstte gösterilir

Bu ilk sürüm yalnızca açıkken çalışır. Mevcut TCP bağlantıları Windows bağlantı tablosundan alınır; önce kurulmuş UDP bağlantıları süreçle eşleşmeyebilir. IPv6 uzantı başlıkları ve parçalanmış paketler limit hesabına girmez. Sınırlı bir bağlantının kuyruğu dolduğunda paketler serbest bırakılmaz: TCP bu paketleri yeniden ister, UDP paketleri kaybolabilir. Bu nedenle özellikle çok düşük sınırlar ve çoklu bağlantılarda aktarım dalgalanabilir. Ayrı Windows servisi ve daha sıkı paket işleme sonraki aşamadır.

Listede gösterilen hız, başarıyla iletilen ağ paketlerinin gerçek süreye göre hesaplanan yaklaşık 3 saniyelik ortalamasıdır. Ağ başlıkları ve yeniden gönderilen paketler bu sayıya dahildir; IDM gibi uygulamaların dosya aktarım hızından biraz farklı olabilir. `—` işareti, o uygulama için Limiter içinde bir sınır kaydedilmediğini belirtir.

Çekirdek kontrollerini çalıştırmak için `dotnet run --project Limiter.EngineChecks\Limiter.EngineChecks.csproj -c Release` kullanın. Bu kontroller paket ayrıştırmayı, gerçek Windows TCP bağlantı eşleştirmesini, zamanlama hesabını ve ayrıştırma maliyetini doğrular. Gerçek ağ yükünde sonuçlar ayrıca test edilmelidir.

## Üçüncü taraf bileşen

WinDivert 2.2.2 (`vendor/WinDivert-2.2.2-A`) kullanılır. WinDivert LGPLv3 veya GPLv2 seçenekleriyle dağıtılır; tam lisans metni `vendor/WinDivert-2.2.2-A/LICENSE` dosyasındadır. Projenin kendi kodu için lisans henüz seçilmemiştir.
