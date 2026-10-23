# E03 — Retry Storm (retry, backoff, jitter, retry bütçesi)

**Türkçe** · [English](README.md)

İlgili yazı: [Retry, Timeout ve Backoff: Ne Zaman Yeniden Denememeliyiz?](https://emregorgulu.com/tr/makaleler/retry-timeout-ve-backoff/) ([English](https://emregorgulu.com/en/articles/retry-timeout-and-backoff/)). Yazıdaki Şekil 1 ve Şekil 2 `results/sample/e03-b` verisinden çizildi.

> **Durum:** Çalıştırılabilir. İki senaryo, tekrar betiği ve aşağıdaki test paketi, burada listelenen sabit sürümlerle 2026-10-22 tarihinde çalıştırıldı (`results/sample/e03-b` içindeki E03-B referans çalıştırması 2026-10-23'te). Sayılar tek bir dizüstü bilgisayardan alındı. Başka makinelerde elde edilecek sonuçlar için bir iddiada bulunulmuyor.

E03 iki senaryodan oluşur. İkisi de aynı simüle bağımlı servisi (10 worker x 40 ms = 250 istek/sn, 100'lük kuyruk, ötesinde 503), makaledeki retry döngüsünü (en fazla 4 deneme, deneme başına 2 sn) ve aynı strateji tanımlarını paylaşır. Farkları, arızanın istemcilere nasıl ulaştığıdır.

| | Senaryo | İş yükü | Arıza | Stratejiler |
| --- | --- | --- | --- | --- |
| **E03-A** | [Geçici arıza ve farklı retry stratejileri](#e03-a--geçici-arıza-ve-farklı-retry-stratejileri) | open-loop Poisson, saniyede 100 işlem | 10 sn boyunca yavaşlayan servis ve %50 rastgele 503 | A, B, C |
| **E03-B** | [Eşzamanlı arıza ve toparlanma baskısı](#e03-b--eşzamanlı-arıza-ve-toparlanma-baskısı) | 200 istemci, closed loop, saniyede yaklaşık 100 işlem | 1,5 sn donma, ardından içerideki her isteği aynı anda hataya düşüren yeniden başlatma, ardından 5 sn yarım kapasite | A, B, C, D |

Stratejiler: **A** anında yeniden deneme (**güvensiz başlangıç senaryosu**, yalnızca arızayı göstermek için var), **B** 200/400/800 ms exponential backoff, **C** full jitter'lı exponential backoff (makaledeki kod), **D** (yalnızca E03-B) C'ye ek olarak istemci tarafında bir retry bütçesi.

### Kısa cevap

Backoff ve jitter, retry'ların zamana nasıl dağıldığını değiştirir. Bunun sisteme etkisi iş yüküne ve kapasite sınırlarına bağlıdır:

- **E03-A, bağımsız gelişler ve 10 sn süren bir arıza.** Her strateji mantıksal işlem başına yaklaşık 1,4-1,55 istek gönderdi; backoff retry'ları zamanda kaydırdı ama sayılarını azaltmadı. Arıza sırasında başlayan işlemlerde en düşük başarı anında yeniden denemede (A), en yüksek başarı B'deydi; C arada kaldı ve beş seed'in dördünde en düşük p99 onundu. Bu sıra beş seed'in her birinde korundu, ama seed her stratejinin sonucunu 5-8 puan oynattı; bu, stratejiler arasındaki 2-5 puanlık farktan büyük.
- **E03-B, eşzamanlı bir arıza** (132-148 istek aynı anda hata alıyor; yeniden başlayan süreç en fazla 110 istek kabul ediyor ve 5 sn boyunca saniyede 125 istek işliyor). Senkronizasyon gerçek: B'nin retry'ları tam olarak +200, +600 ve +1400 ms'de dalgalar halinde geldi; C onları zamana yaydı. Ana yapılandırmada B ve C her işlemi tamamladı ve aynı toplam yükü gönderdi (çalıştırma başına en fazla 13 istek fark). Jitter 100 ms'deki tepe değeri yarıya indirdi (medyan 146'ya karşı 78), saniyedeki tepe değeri değiştirmedi (230'a karşı 229); daha az reddedilme ve daha düşük p99 verdi. Anında yeniden deneme, etkilenen işlemlerin yaklaşık %7'sini kaybetti; hepsini yeniden başlatmadan sonraki 200 ms içinde (biri dışında hepsini ilk 100 ms içinde). Retry bütçesi (D) retry'ları 10'a, tepe değeri 100 ms'de yaklaşık 20 isteğe indirdi ve etkilenen işlemlerin yaklaşık %22'sini anında başarısız bıraktı: kovası (10 jeton) gruptan çok daha küçük.
- **Beklemenin daha çok önemli olduğu yerler (tek çalıştırma, seed 42).** Küçük bir kuyrukla (20; yığılma kabul sınırının yaklaşık 4,5 katı) B'nin dalgaları sürekli dolu bir kuyruğa çarptı: etkilenen işlemlerin B'de %86,9'u, C'de %94,8'i başarılı oldu. Yeniden başlatmadan sonra 2 sn'lik bir outage olduğunda ya da yük soğuk başlangıcın taşıyabileceğinden fazla olduğunda B ve C'nin başarısı hemen hemen aynıydı; C, B'den daha fazla istek gönderdi ve saniyedeki tepe değeri daha yüksekti: full jitter ortalama beklemeyi yarıya indirir; retry'ları dağıtır ama aynı zamanda daha erken getirir.
- **Hiçbir bekleme stratejisi retry trafiğinin miktarını sınırlamadı; yalnızca bütçe sınırladı,** ve bunu işlemleri yeniden denemek yerine başarısız bırakarak yaptı. Bu takaslardan hangisinin kabul edilebilir olduğu tek başına retry formülüne değil, iş yüküne ve kapasite sınırlarına bağlıdır.

## Hızlı başlangıç (tek komut)

Docker ve Compose v2 yeterli. Bu klasörde:

```bash
docker compose up --build
```

Bu tek komut imajları derler, bağımlı servisi ve yük üreticiyi başlatır, önce E03-A'yı (A, B, C), sonra E03-B'yi (A, B, C, D) çalıştırır. Her senaryonun tablolarını yazdırır, `results/e03-a/` ve `results/e03-b/` klasörlerine (`summary.json`, `timeseries.json`, `console.txt`) yazar, ardından iki konteyneri de durdurur ve `docker compose up` 0 çıkış koduyla sonlanır. Deney makinesinde ilk build'den sonra 246 sn sürdü (ilk build birkaç dakika daha ekler). Sonrasında duran konteynerleri ve ağı kaldırın:

```bash
docker compose down
```

İmajları da silmek için `docker compose down --rmi local` kullanın. Her parametre kabuktan değiştirilebilir, örneğin `E03_SEED=7 docker compose up --build`, ya da yalnızca bir senaryo: `E03_SCENARIOS=b docker compose up --build` ([E03-A](#parametreler-ve-gerekçeleri) ve [E03-B](#parametreler-ve-gerekçeleri-1) parametre tablolarına bakın). İki senaryoyu beş seed ile tekrarlamak için: `./repeat-e03.sh` (yaklaşık 21 dakika, [Birden fazla seed ile tekrarlama](#birden-fazla-seed-ile-tekrarlama) bölümüne bakın).

> Çalıştırma sırasında dizüstü bilgisayarınız uykuya geçerse Docker'ın sanal makinesi de uyur ve `docker compose up` yalnızca bekler. Deney makinesinde bu, E03-A'nın ilk çalıştırmalarında iki kez yaşandı; o çalıştırmalar sonuçlardan çıkarıldı. macOS'ta `caffeinate -di docker compose up --build` bunu önler; `./repeat-e03.sh` bunu kendisi yapar.

Çalışırken gördükleriniz (kısaltılmış):

```text
loadgen-1  | [A] Immediate retry (UNSAFE BASELINE): wait none: next attempt at once
loadgen-1  |   [A] t= 5-10 s  attempts sent    104/s   ops succeeded   103/s   failed     0/s
loadgen-1  |   [A] t=10-15 s  attempts sent    215/s   ops succeeded    60/s   failed    21/s
...
loadgen-1  | [B] Exponential backoff: wait 200, 400, 800 ms (no jitter); total 5 s per operation
loadgen-1  |  [B] requests at the dependency per 100 ms, 9.5-12.0 s (restart at 10.0 s): 7 7 8 9 11 11 3 143 6 11 8 36 4 6 1 0 4 5 2 8 3 5 2 5 3
```

İlerleme satırları istemci tarafının anlık sayılarıdır. Esas alınması gerekenler, en sondaki tablolar ve JSON dosyalarıdır.

## E03-A — Geçici arıza ve farklı retry stratejileri

İlk E03 deneyi, değiştirilmeden: aynı parametreler, aynı davranış, aynı çıktı biçimi (şema v1). Yalnızca çıktı klasörü `results/e03-a/`, commit edilmiş örneği de `results/sample/e03-a/` olarak taşındı (bayt bayt aynı).

### Soru

Bağımlı bir servis on saniye boyunca bozuluyor. İstemcileri başarısız çağrıları yeniden deniyor. Yeniden denemeler ne kadar ek trafik yaratır, mantıksal işlemlerin ne kadarı yine de başarılı olur ve denemeler arasındaki bekleme stratejisi bunu değiştirir mi?

Üç istemci stratejisi **aynı** bağımlı servise karşı, **aynı** iş yüküyle (birebir aynı geliş takvimi) ve **aynı** enjekte edilen arızayla karşılaştırılır:

| | Strateji | 2., 3. ve 4. denemeden önceki bekleme | Etiket |
| --- | --- | --- | --- |
| **A** | Anında yeniden deneme | yok | **Güvensiz başlangıç senaryosu.** Yalnızca arızayı göstermek için var. |
| **B** | Jitter'sız exponential backoff | 200, 400, 800 ms | |
| **C** | Exponential backoff + full jitter + sınırlar | [0, 200], [0, 400], [0, 800] ms aralığında rastgele; mantıksal işlem başına en fazla 5 sn | Makaledeki kod |

Üçü de aynı retry döngüsünü kullanır: en fazla **4 deneme**, **deneme başına 2 sn zaman aşımı**, ve yalnızca **408, 429, 502, 503, 504**, deneme zaman aşımı ve `HttpRequestException` durumunda yeniden deneme. Farklı olan yalnızca bekleme davranışı ve C'nin toplam süre sınırıdır.

#### Kısa cevap (E03-A, aşağıdaki çalıştırmalardan)

- **Fırtınayı yaratan retry'lardır.** Arıza penceresinin doygun kısmında bağımlı servis, **her stratejide** sunulan yükün yaklaşık **3 katını** aldı (saniyede yaklaşık 100 yeni işleme karşılık yaklaşık 280-310 istek/sn). Çalıştırmanın tamamında her strateji mantıksal işlem başına yaklaşık 1,5 istek gönderdi.
- **Backoff yeniden deneme sayısını azaltmadı, zamana yaydı.** Deneme sınırı aynıyken ve arıza her backoff süresinden çok daha uzun sürerken A, B ve C'nin çoğaltma katsayıları birbirinden en fazla 0,07x farklıydı (1,48-1,55x).
- **Başarıda en kötüsü anında yeniden denemeydi.** Arıza sırasında başlayan işlemlerin %68'i başarılı oldu. Bu oran C'de %73, B'de %76 idi. Anında yapılan yeniden deneme, kendisini az önce reddeden aynı dolu kuyruğa çarpar.
- **Jitter burada yardımcı olmadı. B, C'den biraz daha sık başarılı oldu,** çünkü B toplamda her zaman 1,4 sn bekler, full jitter ise ortalamada bunun yarısı kadar bekler. Böylece B'nin son denemelerinin daha fazlası pencere kapandıktan sonraya denk gelir. En düşük p99 gecikmesi C'deydi (B'nin 3,3-3,6 sn'sine karşı yaklaşık 3,0 sn).
- **Toparlanma üçünde de anlıktı** (0-1 sn), çünkü bu bağımlı servis sınırlı bir kuyrukla yük atıyor. Derin bir kuyrukla ([Duyarlılık](#duyarlılık-çalıştırmaları) bölümüne bakın) tablo değişir: zaman aşımları ve boşa giden iş ortaya çıkar, C'nin toplam süre sınırı da başarıdan feragat edip gecikmeyi sınırlar.

Bunların hiçbiri jitter'ın işe yaramadığını göstermez. Buradaki arıza senkronize değildir; jitter asıl olarak çok sayıda istemcinin aynı anda hata aldığı durumlarda işe yarar. Gösterdiği şey, **backoff ve jitter'ın tek başına retry trafiğini sınırlamadığıdır**. Bunu bir retry bütçesi ya da circuit breaker yapar. E03-A ikisini de uygulamaz; E03-B bir retry bütçesi ekler (D stratejisi).

### Deney nasıl işliyor

```text
loadgen (tek konteyner)                                dependency (tek konteyner)
  A, B, C için aynı Poisson geliş takvimi                10 worker x 40 ms  = 250 istek/sn
  her mantıksal işlem = GetWithRetryAsync(...)  --->     100'lük kuyruk, ötesi: anında 503
  denemeler bir DelegatingHandler'da sayılır             her sıfırlamadan sonra 10-20 sn arası arıza:
  gecikme planlanan geliş anından ölçülür (açık döngü)     istek başına 80 ms (125 istek/sn)
                                                           işlenen isteklerin %50'sine 503
                                                         saniyelik sayaçlar burada ölçülür
```

Yük üretici her strateji için şunları yapar:

1. Yeni bir `HttpClient` oluşturur (önceki stratejiden bağlantı devralınmaz).
2. Bağımlı servisi arıza penceresi kapalıyken sıfırlar ve aynı hızda 2 sn boyunca tek denemeli ısınma trafiği gönderir (ölçülmez: JIT ve bağlantı kurulumu).
3. Bağımlı servis boşalana kadar bekler ve onu yeniden sıfırlar. Bu sıfırlama, bağımlı servisin saatini başlatır ve arıza penceresini kurar.
4. 3038 mantıksal işlemi önceden hesaplanmış Poisson zamanlarında başlatır (açık döngü: yeni gelişler önceki işlemleri beklemez).
5. Tüm işlemlerin bitmesini, ardından bağımlı servisin boşalmasını bekler ve servisin saniyelik sayaçlarını okur.

Trafik sayıları istemciden değil, **bağımlı servisten** (`/admin/stats`) gelir. İstemcinin kendi deneme sayısı çapraz kontrol için tutulur. Her çalıştırmada ikisi birebir aynı çıktı: yolda kaybolan deneme olmadı.

#### Bağımlı servis

`src/Dependency`, tek uç noktası `GET /work` olan bir ASP.NET Core minimal API'dir:

- **Sınırlı kapasite.** 10 worker, 40 ms işlem süresi, yani saniyede 250 istek. İş CPU değil bir zamanlayıcıdır (`Task.Delay`), bu yüzden kapasite her makinede aynıdır.
- **Sınırlı kuyruk.** En fazla 100 istek FIFO sırasıyla worker bekler. 111. istek anında `503` ile reddedilir (yük atma, hiç iş yapılmaz).
- **Her sıfırlamadan 10-20 sn sonrası arıza penceresi.** İşlem süresi iki katına, 80 ms'ye çıkar (kapasite 125 istek/sn). İşlenen isteklerin %50'si işi *yaptıktan sonra* `503` ile yanıtlanır ("enjekte edilen geçici hata"). Bir isteğin bozulup bozulmayacağı, işlenmeye başladığı anda belirlenir.
- **Terk edilen iş iptal edilmez.** İstemci zaman aşımına uğrarsa servis isteği yine de tamamlar ve `abandoned` olarak sayar; birçok gerçek sunucu da böyle davranır. Ana yapılandırmada bu hiç yaşanmaz (hiçbir deneme zaman aşımına uğramaz). Derin kuyruk varyantında önem kazanır.
- **Yalnızca laboratuvar için, kimlik doğrulamasız yönetim uç noktaları:** `POST /admin/reset`, `GET /admin/stats`, `POST /admin/shutdown`. Çalışma sırasında `curl -s http://127.0.0.1:18490/admin/stats | jq .totals` ile izleyebilirsiniz.

#### İstemci kodu (ve makaleden farkları)

`src/LoadGenerator/RetryLoop.cs`, makaledeki `GetWithRetryAsync` fonksiyonunu içerir ve A, B ve C bunu ortak kullanır. Değişen yerler **yalnızca** şunlar:

| Makale | Burada | Neden |
| --- | --- | --- |
| `var capMs = Math.Min(200 * (1 << (attempt - 1)), 2000);`<br>`var delay = TimeSpan.FromMilliseconds(Random.Shared.Next(0, capMs + 1));` | `var delay = delayAfterFailedAttempt(attempt);` | A, B ve C arasında değişen tek şey beklemedir. C için delegate `Backoff.FullJitter`'dır ve makaledeki iki satırı aynen içerir... |
| `Random.Shared` | Her mantıksal işlem için bir `new Random(seed * 1_000_003 + operationIndex)` | ...tek fark, çalıştırmaların tekrarlanabilir olması için seed verilmiş bir `Random`'dır. Paylaşılan bir `Random` thread-safe değildir. İşlem başına seed, *i* numaralı işleme her çalıştırmada aynı jitter dizisini verir. |
| `const int maxAttempts = 4;`, `TimeSpan.FromSeconds(2)` | `RetryLoop.MaxAttempts = 4`, `RetryLoop.AttemptTimeout = 2 sn` | Değerler aynı. Rapor ve testler kullanabilsin diye dışarı açıldılar. |
| toplam işlem süresi sınırlandırılır | C'yi çağıran kod, işlem başladıktan **5 sn** sonra iptal edilen bir `cancellationToken` geçirir | Döngünün içinde değişiklik yok: makaledeki kod bu token'da zaten durur (token retry filtresinin dışında tutulur ve `Task.Delay` onu dikkate alır). A ve B'de toplam süre sınırı yoktur. Onların tek sınırı örtüktür: 4 x 2 sn + 1,4 sn = 9,4 sn. |

Makalede olmayan iki ayrıntı daha var. `HttpClient.Timeout` sonsuz yapılır, böylece tek zaman aşımı deneme başına 2 sn'dir. Ölçüm için eklenen bir `DelegatingHandler` her denemeyi sayar ve yanıt gövdesini denemenin içinde okur; böylece gövdenin okunması da 2 sn'lik zaman aşımına dahil olur.

**B** stratejisi, makaledeki üst sınır formülünün jitter'sız halidir: `Math.Min(200 * (1 << (attempt - 1)), 2000)` 200, 400 ve 800 ms verir. Yani B ile C'nin üst sınırları aynıdır; full jitter bu sınırların altından eşit olasılıkla seçer. Bunun, sonuçları okurken önemli bir sonucu var: **Full jitter'da ortalama bekleme, üst sınırın yarısıdır.** Üç bekleme de gerçekleştiğinde C toplamda ortalama yaklaşık 0,7 sn bekler, B ise her zaman 1,4 sn bekler. C "en sabırlı, en yavaş" strateji değildir.

### Parametreler ve gerekçeleri

Tüm değerler `compose.yaml` içindedir (`x-e03-parameters`). Koddaki varsayılanlar aynıdır ve her değer `summary.json`'a kaydedilir.

| Değişken | Değer | Neden bu değer |
| --- | --- | --- |
| `E03_RATE` | 100 işlem/sn | Yuvarlak bir sayı. Normal kapasitenin %40'ı; sağlıklı bir servis için rahat bir pay. |
| `E03_DURATION_S` | 30 sn | 10 sn sağlıklı, 10 sn arıza, 10 sn sonrası. Önce, sırası ve sonrasını görmeye yetecek kadar uzun; üç stratejinin yaklaşık 100 sn'de bitmesine yetecek kadar kısa. |
| `E03_SEED` | 42 | Geliş takvimi, jitter ve enjekte edilen arıza çekilişleri. Aynı seed, her strateji için aynı geliş takvimini verir. |
| `E03_WORKERS`, `E03_SERVICE_TIME_MS` | 10 x 40 ms = 250 istek/sn | Üzerine kolay düşünülür: kapasite = worker sayısı / işlem süresi. |
| `E03_QUEUE_LIMIT` | 100 | Normal kapasitede 0,4 sn'lik, pencere içinde 0,8 sn'lik iş. İkisi de 2 sn'lik deneme zaman aşımının altında. Böylece kuyruktaki hiçbir istek terk edilmez ve karşılaştırma yalnızca bekleme davranışını ölçer. Derin kuyruk ayrıca denendi. |
| `E03_FAILURE_START_S`, `E03_FAILURE_END_S` | 10 sn - 20 sn | On saniye, buradaki her backoff süresinden (işlem başına en fazla 1,4 sn) çok daha uzun. Bu kısa bir kesinti değil, süregiden bir olay. |
| `E03_FAILURE_SERVICE_TIME_MS` | 80 ms (125 istek/sn) | "Yarı kapasite." Asıl yük (100/sn) hâlâ sığar (%80 kullanım). Retry olmasaydı bağımlı servis aşırı yüklenmezdi. |
| `E03_FAILURE_ERROR_RATE` | 0,5 | "İşlenen isteklerin yarısı başarısız." Retry'larla gereken deneme sayısı düşen kapasiteyi aşar (%50 hata ve en fazla 4 denemeyle işlem başına 1,875 deneme, yani 125'e karşı yaklaşık 190 istek/sn) ve bağımlı servis yük atmaya başlar. Bu deneyin konusu olan retry fırtınası budur. |
| `E03_C_DEADLINE_MS` | 5000 | İki tam deneme zaman aşımı artı backoff. Yalnızca denemeler zaman aşımına uğradığında devreye girer. Ana yapılandırmada hiç devreye girmedi (0 işlem). |
| `E03_WARMUP_S` | 2 sn | Bu hızda JIT ve bağlantı kurulumu için yeterli. |
| `E03_STRATEGIES` | `A,B,C` | Çalıştırma sırası. Ters sıra da denendi (ölçülebilir bir etkisi yok). |
| `E03_ATTEMPT_LOG` | `false` | `true` olursa ayrıca `results/e03-a/attempts.jsonl` (ve `results/e03-b/attempts.jsonl`) yazılır: deneme başına bir JSON satırı (`strategy`, `operation_id`, `attempt`, `phase`, `outcome`, `sent_ms`, `duration_ms`). Strateji başına yaklaşık 4.500 satır. |
| Toparlanma ölçütü | 3 sn boyunca ≥ %95 | Tanım aşağıda. Kodda sabittir (`Metrics.cs`). |

**Parametreler nasıl seçildi (açıkça).** İlk yapılandırmada hata oranı %25'ti. Bu yapılandırmada kuyruk ancak pencerenin sonuna doğru doldu. Çoğaltma katsayısı üç stratejide de 1,15-1,18x oldu ve farklar küçük kaldı (tablo [Duyarlılık](#duyarlılık-çalıştırmaları) bölümünde). Bu bir retry fırtınası değil, hafif bir performans düşüşüydü. Ardından hata oranı %50'ye çıkarıldı ("yarı kapasite, yarı hata"), böylece retry'lar düşen kapasiteyi aşıyor. Bundan sonra hiçbir parametre bir stratejiyi öne çıkarmak için değiştirilmedi; sıralama da buna bağlı çıkmadı.

### Gözlemlenen sonuçlar

Makine: MacBook (Apple Silicon), Docker Desktop 28.4.0 / Compose 2.39.4; Docker sanal makinesinin gördüğü 12 CPU ve yaklaşık 8 GB bellek. Tarih: 2026-10-22. Varsayılan parametrelerle (seed 42) dört geçerli `docker compose up --build` çalıştırması: art arda üç çalıştırma ve imaj silindikten sonra yapılan son bir doğrulama çalıştırması.

Üçüncü çalıştırmanın konsol çıktısı (`results/sample/e03-a/console.txt` içinde de var):

```text
====================================================================================================
E03 Retry Storm: same workload, same failure window, three client strategies
====================================================================================================
Strategy                       Requests   Ampl.  Success  in window      p95      p99 Peak rps Recovery
----------------------------------------------------------------------------------------------------
A  immediate retry (UNSAFE)        4535   1.49x    89.1%      68.4%  1833 ms  3542 ms      377      0 s
B  backoff 200/400/800 ms          4551   1.50x    92.0%      76.3%  2631 ms  3378 ms      339      0 s
C  backoff + jitter + limits       4648   1.53x    91.2%      73.7%  2129 ms  3033 ms      375      0 s
----------------------------------------------------------------------------------------------------
Same 3038 logical ops per strategy (Poisson 100/s, 30 s, seed 42). Dependency 250 req/s (10 x 40 ms), queue 100, then 503.
Failure 10-20 s: 125 req/s (80 ms), 50% injected 503. All: 4 attempts max, 2 s each; C also 5 s per op.
Requests: received by the dependency. Ampl.: requests per logical op. Success: ops answered 200 (in window: ops
started during the failure). p95/p99: end-to-end latency, all ops. Recovery: s after the window until per-second success >= 95% for 3 s.
```

**Dört çalıştırma arasındaki değişim** (en düşük - en yüksek):

| | Requests | Ampl. | Success | in window | p95 | p99 | Peak rps | Recovery |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| A | 4508-4549 | 1,48-1,50x | %89,1-89,4 | %68,1-68,8 | 1814-1844 ms | 3443-3553 ms | 377-387 | 0 sn |
| B | 4551-4640 | 1,50-1,53x | %91,6-92,0 | %75,2-76,3 | 2415-2832 ms | 3323-3620 ms | 339-358 | 0-1 sn |
| C | 4610-4696 | 1,52-1,55x | %90,8-91,2 | %72,5-73,7 | 2070-2195 ms | 2969-3033 ms | 368-379 | 0 sn |

İstemci ve bağımlı servis davranışı aynı olan (yalnızca raporlamaya yönelik iki alan eklenmeden önceki) üç çalıştırma daha aynı aralıklarda kaldı; örneğin pencere içi başarı A'da %68,9-69,0, B'de %75,4-76,6, C'de %72,7-73,4 idi. Her çalıştırmada pencere içi başarı sıralaması B > C > A çıktı. Aradaki farklar (B ile C arasında 2,6-3,4 puan, C ile A arasında 4,0-5,3 puan), her stratejinin çalıştırmalar arasındaki oynamasından (en fazla 1,2 puan) büyüktü. Çoğaltma katsayısındaki farklar küçük (bir çalıştırma içinde en fazla 0,07x), ama A < B < C sırası her çalıştırmada korundu. Bu, C'nin ortalamada daha kısa beklemelerinin daha fazla denemeyi pencere içinde bırakmasıyla uyumlu. Bir saniyedeki en yüksek istek sayısı her çalıştırmada B'de en düşüktü (368-387'ye karşı 339-358).

Tüm çalıştırmalarda hiçbir deneme zaman aşımına uğramadı, C'nin toplam süre sınırı hiçbir işlemi kesmedi ve `abandoned` 0 oldu. Bağımlı servisin en yüksek in-flight değeri 110-111'di, yani tamamen doluydu. İki çalıştırma daha, sırasında dizüstü bilgisayar boşta kalıp uykuya geçtiği için sonuçlardan çıkarıldı (`pmset -g log` ile doğrulandı). Sayıları diğerleriyle tutarlıydı, ama duraklatılmış bir çalıştırma temiz bir ölçüm sayılmaz.

#### Sayılar ne söylüyor

1. **Fırtına yükten değil, retry'lardan geliyor.** Sunulan yük yaklaşık 100 işlem/sn'de kaldı. Örnek çalıştırmanın 15-19. saniyelerinde bağımlı servis ortalama 281 (A), 310 (B) ve 313 (C) istek/sn aldı ve bunların %57-61'ini anında reddetti (`rejected503`). En yüksek in-flight değeri 110'da kaldı (tüm worker'lar meşgul, kuyruk dolu).
2. **Bekleme stratejisi hacmi değiştirmedi.** Pencerede başlayan her işlemin aynı dört denemelik hakkı var ve arıza her backoff süresinden çok daha uzun sürüyor. Bu yüzden kararlı durumda her strateji aynı denemeleri gönderir, yalnızca farklı anlarda. Çoğaltma katsayıları 0,07x içinde kaldı (çalıştırmanın tamamında 1,48-1,55x; pencere içinde başlayan işlem başına yaklaşık 2,4-2,6 deneme).
3. **Anında yeniden deneme, denemelerini boşa harcar.** A'nın bir denemesi kuyruk dolu olduğu için reddedildiğinde, yeniden denemesi bir milisaniye sonra gelir, aynı dolu kuyruğu görür ve yine reddedilir. Dört denemesini en hızlı tüketen A oldu (en düşük p95, ama B'nin 242-254'üne karşı 323-332 başarısız işlem). Pencereden hemen sonra, 20. saniyede gelen işlemlerde %100'ün altında kalan tek strateji A'ydı (%93-96): yeniden denemeleri hâlâ boşalmakta olan birikmiş işe çarptı.
4. **B, jitter zararlı olduğu için değil, daha uzun beklediği için C'den daha başarılı oldu.** 13-16. saniyelerde gelen işlemlerde geliş saniyesine göre başarı (`successPercentByArrivalSecond`) üç stratejide de aynı %38-75 bandındaydı. Tutarlı fark, pencerenin son saniyelerinde gelen işlemlerden kaynaklanıyor. 18. saniyede B %90-95'e, C %80-83'e ulaştı (A: %67-73). 19. saniyede B %100'e, C %94-99'a ulaştı (A: %57-61). Doygun pencerede bir işlem, kabul edilen her denemede yaklaşık 0,9 sn'ye kadar zaman harcar. Buna B'nin sabit 1,4 sn'lik beklemesi eklenince, B'nin son denemeleri arıza bittikten sonraya denk gelir. C'nin full jitter'ı ortalamada bunun yarısı kadar bekler; bu yüzden denemelerinin daha fazlası hâlâ pencerenin içindedir.
5. **En düşük kuyruk gecikmesi C'deydi.** p99 yaklaşık 3,0 sn'ydi; B'de 3,3-3,6 sn, A'da 3,4-3,6 sn. Sebebi, beklemelerinin ortalamada daha kısa olması.
6. **Toparlanma anlıktı.** Sınırlı kuyruk, normal kapasitede en fazla 0,4 sn'lik iş tutar. Bu yüzden birikmiş iş, her stratejide pencereden sonraki ilk saniye içinde eridi.

### Beş seed ile tekrar

`./repeat-e03.sh`, E03-A'yı da 42, 1, 2, 3 ve 4 seed'leriyle çalıştırdı: yukarıdaki yapılandırma, seed dışında değiştirilmeden (5 çalıştırma, mevcut kod, aynı makine, 2026-10-22). Seed, geliş takvimini, jitter değerlerini ve enjekte edilen hata çekilişlerini değiştirir. Beş çalıştırma üzerinden medyan [en düşük - en yüksek], `results/sample/repeat.txt` dosyasından (tek tek her değer orada listeleniyor):

| | Requests | Ampl. | Success | in window | p95 | p99 | Peak rps | Recovery |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| A | 4274 [4164-4520] | 1,44 [1,40-1,49] | %90,6 [89,2-91,9] | %71,4 [68,5-74,9] | 1811 [1789-1820] ms | 3529 [3503-3608] ms | 375 [296-387] | 0 sn |
| B | 4330 [4164-4656] | 1,47 [1,40-1,55] | %93,3 [91,9-94,0] | %79,6 [76,0-81,4] | 2762 [2607-3016] ms | 3642 [3285-3881] ms | 330 [282-350] | 0 sn |
| C | 4435 [4293-4689] | 1,50 [1,44-1,55] | %91,9 [90,2-93,3] | %75,3 [71,5-79,2] | 2127 [2093-2135] ms | 3087 [2993-3296] ms | 351 [315-391] | 0 sn |

- **Seed'ler arasındaki değişim, stratejiler arasındaki farklardan büyük.** Seed, her stratejinin pencere içi başarısını 5-8 puan oynatıyor (A %68,5-74,9, B %76,0-81,4, C %71,5-79,2); aralıklar örtüşüyor.
- **Her seed'in içinde sıra değişmedi.** Pencere içi başarı beş çalıştırmanın hepsinde B > C > A oldu (B, C'nin 2,1-5,2 puan; C, A'nın 3,0-5,1 puan üzerinde). Çoğaltma katsayısı her çalıştırmada A ≤ B ≤ C idi. En düşük p99 beş çalıştırmanın dördünde C'deydi (seed 4: B 3285 ms, C 3296 ms). Bir saniyedeki en düşük tepe değer beş çalıştırmanın dördünde B'deydi (seed 3: A 296, B 330).
- Hiçbir deneme zaman aşımına uğramadı, hiçbir istek terk edilmedi (`abandoned`), toparlanma her çalıştırmada 0 sn oldu. Bu tekrarın seed 42 çalıştırması (pencere içi başarı %68,7 / %76,0 / %73,4) önceki dört çalıştırmanın aralıkları içinde kaldı.

Beş çalıştırma bir anlamlılık testi değildir. Yukarıdaki sıralamanın seed 42'ye özgü olmadığını, ve tek bir çalıştırmanın aradaki farkların ne kadar büyük olduğunu söyleyemeyeceğini gösterir.

### Duyarlılık çalıştırmaları

Her biri, gösterilen değişkenle `docker compose up` kullanılarak bir kez çalıştırıldı. İstemci ve bağımlı servis davranışı ana çalıştırmalarla aynıydı.

| Varyant | Komut | A pencere içi / p99 | B pencere içi / p99 | C pencere içi / p99 | Not |
| --- | --- | --- | --- | --- | --- |
| Farklı seed | `E03_SEED=7` | %66,5 / 3527 ms | %73,6 / 3531 ms | %70,4 / 3077 ms | Aynı sıralama. Çoğaltma 1,50 / 1,52 / 1,56x |
| Ters sıra | `E03_STRATEGIES=C,B,A` | %68,7 / 3528 ms | %76,5 / 3632 ms | %73,1 / 3060 ms | Sıra etkisi yok |
| Daha hafif arıza | `E03_FAILURE_ERROR_RATE=0.25` | %96,0 / 1936 ms | %99,4 / 2648 ms | %98,0 / 2229 ms | Fırtına değil, hafif performans düşüşü: çoğaltma 1,15 / 1,15 / 1,18x |
| Derin kuyruk | `E03_QUEUE_LIMIT=400` | %70,3 / 6756 ms | %89,5 / 7648 ms | %69,3 / 5003 ms | Zaman aşımları ortaya çıkıyor; aşağıya bakın |

**Derin kuyruk (`E03_QUEUE_LIMIT=400`).** Pencere içinde kuyruk bekleme süresi artık 3,2 sn'ye ulaşıyor ve 2 sn'lik deneme zaman aşımını aşıyor. 18-19. saniyeden pencere bitene kadar saniyede başarılı olan işlem sayısı sıfıra düşüyor; bağımlı servis ise %100 meşgul. Servis, istemcileri çoktan vazgeçmiş 540-660 isteği tamamlıyor (`abandoned`). Bu, retry ve zaman aşımlarının yol açtığı bir tıkanma çöküşü (congestion collapse). Toparlanma A'da 1 sn, B'de 2 sn, C'de 3 sn sürdü. C'nin 5 sn'lik toplam süre sınırı 183 işlemi kesti. Bu sınır C'nin p99'unu 5,0 sn'de tuttu (A: 6,8 sn, B: 7,6 sn), ama pencere içi başarısı %69'a düştü. Toplam süre sınırının yaptığı takas budur: yavaş ve belirsiz bir başarı yerine hızlı ve kesin bir başarısızlık. B'nin daha uzun sabit beklemeleri, burada da daha fazla işlemin pencereyi atlatmasını sağladı.

## E03-B — Eşzamanlı arıza ve toparlanma baskısı

### Soru

Çok sayıda istemci **aynı anda** hata alıyor ve kapasitesi sınırlı bir bağımlı servis, bu istemcilerin retry'ları geri gelirken toparlanmaya çalışıyor. Anında yeniden deneme, exponential backoff, full jitter ve bir retry bütçesi, toparlanan servise ulaşan isteklerin *yoğunluğunu* nasıl değiştirir: kaç istek gelir, zamanda ne kadar yığılır, aynı anda kaçı içeride olur ve mantıksal işlemlerin kaçı yine de başarılı olur?

E03-A bu soruyu cevaplayamazdı: orada gelişler Poisson dağılımlı, hatalar rastgeledir; hatalar zamana yayılır ve jitter'ın dağıtacağı bir senkronizasyon yoktur. E03-B bu senkronizasyonu bilerek oluşturur ve gerçekten oluştuğunu 100 ms'lik dilimlerle doğrular.

### Tasarım ve gerekçesi

```text
loadgen: 200 istemci, closed loop                       dependency: 10 worker x 40 ms = 250 istek/sn, kuyruk 100 (en fazla 110 içeride)
  her istemci: bir mantıksal işlem, sonra                 0    - 8,5 sn  normal
  1-3 sn düşünme (uniform), sonra sıradaki işlem          8,5  - 10,0 sn DONMA: hiçbir istek tamamlanmaz, reddedilmez;
  saniyede yaklaşık 100 işlem, E03-A ile aynı                            içerideki ve yeni gelen her istek bekletilir
  E03-A ile aynı retry döngüsü ve stratejiler             10,0 sn        YENİDEN BAŞLATMA: içerideki her istek aynı anda 503 alır
  HER strateji için işlem başına toplam 5 sn              10,0 - 15,0 sn SOĞUK BAŞLANGIÇ: yeni süreç, istek başına 80 ms (125 istek/sn)
                                                          15,0 sn -      yeniden normal
```

- **Sabit bir istemci topluluğu, closed loop.** 200 istemcinin her biri aynı anda tek bir mantıksal işlem yürütür ve bir sonrakinden önce 1-3 sn (uniform, istemci başına seed'li) düşünür. 200 istemci / 2 sn, saniyede yaklaşık 100 işlem eder; bu, E03-A'daki sunulan yükle (kapasitenin %40'ı) aynıdır. Closed loop, sabit bir çağıran kümesinin (worker thread'leri, kullanıcılar, upstream instance'lar) davranışıdır: retry bekleyen bir istemci yeni iş göndermez. Her istemci [0, 3) sn aralığında rastgele bir anda başlar; böylece topluluk adım adım aynı anda başlamaz. Bunun bir sonucu var: mantıksal işlem sayısı stratejiler arasında biraz farklıdır (hızlı başarısız olan bir strateji, istemcilerinin daha fazla işlem başlatmasına izin verir). Bu yüzden rapor her oranın yanında işlem sayısını da gösterir.
- **Önce donma, sonra yeniden başlatma: eşzamanlı arıza.** 8,5. saniyeden itibaren bağımlı servis hiçbir şeyi tamamlamaz; takılan bir süreç gibi davranır (örneğin kilitlenmiş bir lock, uzun bir duraklama ya da bağlantıları tutan bir failover). İstekler birikir; kabul sınırı bile uygulanmaz, çünkü takılmış bir süreç kendi kontrollerini çalıştıramaz. 10,0. saniyede süreç yeniden başlar ve **içerideki her istek aynı anda hata alır** (503). Varsayılan zaman çizelgesiyle bu, aşağıdaki çalıştırmalarda 132-148 istek etti (`heldAtRestart`), yani topluluğun yaklaşık %70'i. Bu isteklerin retry'larının hepsi aynı andan başlar.
- **Donma, 2 sn'lik deneme zaman aşımından kısadır.** Daha uzun olsaydı, bekletilen her istek gönderilmesinden 2 sn sonra, tek tek, geliş zamanı gibi zamana yayılarak zaman aşımına uğrardı. 1,5 sn, hepsini yeniden başlatmaya kadar bekletir.
- **Yeniden başlatma yeni bir süreç oluşturur.** Bu sürecin kendi worker'ları ve boş bir kuyruğu vardır; yeniden başlatmayla sonlandırılan istekler onun kabul sınırına (110 = 10 worker + 100 kuyruk) sayılmaz.
- **Azaltılmış kapasiteyle soğuk başlangıç.** Yeniden başlatmadan sonraki 5 sn boyunca bir istek 40 ms yerine 80 ms sürer (125 istek/sn); cache'ler, havuzlar ve JIT ısınırken olduğu gibi. 125 istek/sn sunulan yükün (yaklaşık 100/sn) üzerindedir; yani retry'lar ve ani yığılma olmasa servis baş ederdi. Ama yığılmayı bir anda karşılamaya yetmez: yaklaşık 140 isteklik grup kabul sınırı olan 110'u zaten aşar ve dolu bir kuyruk soğuk hızda 0,88 sn'de boşalır.
- **Ana yapılandırmada yeniden başlatmadan sonra reddetme dönemi yok (outage 0 ms).** Her isteğin reddedildiği bir dönem, sonucu sabitlerin rastlantısına bağlardı: B'nin toplam backoff süresinden (0,2 + 0,4 + 0,8 = 1,4 sn) uzun bir outage'da, grubun retry'ları her stratejide outage'ın içine düşer; daha kısa bir outage'da ise B'nin son dalgası tesadüfen hemen sonrasına denk gelir. Donma zaten bir kesinti sağlıyor. 2 sn'lik reddetme dönemi bir duyarlılık varyantı olarak çalıştırıldı (`E03B_OUTAGE_MS=2000`).
- **Her strateji için aynı koşullar:** aynı 200 istemci ve aynı düşünme süresi dizileri, aynı zaman çizelgesi, en fazla 4 deneme, deneme başına 2 sn ve **A, B, C ve D'nin hepsi için mantıksal işlem başına toplam 5 sn** (E03-A'da bu süre sınırı yalnızca C'de var).
- **Kapasite bir zamanlayıcıdır**, E03-A'da olduğu gibi: sonuçlar host makinenin CPU'suna bağlı değildir.
- **Kopan bağlantılar yerine 503.** Gerçek bir yeniden başlatma TCP bağlantılarını düşürür. Burada bağımlı servis 503 döner; retry döngüsü ikisine de aynı davranır (ikisi de yeniden denenir), bağlantı havuzu bozulmaz ve sayım kesin kalır. Yeniden bağlanma fırtınaları modellenmedi.

### Stratejiler ve retry bütçesi

| | Strateji | 2., 3. ve 4. denemeden önceki bekleme | Diğer sınırlar |
| --- | --- | --- | --- |
| **A** | Anında yeniden deneme (**güvensiz başlangıç senaryosu**) | yok | 4 deneme, her biri 2 sn, işlem başına 5 sn |
| **B** | Exponential backoff | 200, 400, 800 ms | aynı |
| **C** | Exponential backoff + full jitter | [0, 200], [0, 400], [0, 800] ms aralığında rastgele | aynı |
| **D** | C + retry bütçesi | C ile aynı (aynı işlem için aynı rastgele değerler) | aynı, ayrıca: her retry için bütçeden bir jeton gerekir |

A, B ve C, süre sınırı hepsine verilmiş E03-A stratejileridir. D dördüncü bir strateji olarak eklendi; böylece A, B ve C, E03-A ile karşılaştırılabilir kalıyor ve bütçenin etkisi, yalnızca bütçe bakımından farklı olan C'ye göre okunabiliyor.

**Retry bütçesi (D).** 200 istemcinin paylaştığı tek bir token bucket; çok sayıda eşzamanlı isteği olan tek bir çağıran servisin sahip olacağı gibi:

- her yeni mantıksal işlem **0,1 jeton** ekler (kova en fazla **10** jeton tutar ve dolu başlar),
- her retry tam bir jetona ihtiyaç duyar ve onu harcar; jeton yoksa işlem son hatayla hemen sona erer (`retry_budget_exhausted`),
- dolayısıyla herhangi bir sürede retry sayısı en fazla 10 + 0,1 x yeni işlem olur: **ilk denemelerin yaklaşık %10'u** ve 10'luk bir ani artış payı.

Oran, Google SRE kitabındaki istemci başına retry bütçesini izler ("Handling Overload" bölümü: bir istek, yalnızca retry'lar isteklerin %10'unun altındayken yeniden denenir). Ekle/harca biçimindeki token bucket, Finagle'ın `RetryBudget`'ının (`percentCanRetry`) biçimidir. gRPC'nin retry throttling'i (gRFC A6, örnek değerler `maxTokens` 10, `tokenRatio` 0.1) yakın bir varyanttır: başarıda jeton ekler, hatada jeton düşer. AWS Builders' Library'deki "Timeouts, retries, and backoff with jitter" yazısı, retry'ları yerel olarak bir token bucket ile sınırlamayı anlatır. Bu kütüphanelerin hiçbiri kullanılmadı; `src/LoadGenerator/RetryBudget.cs` bu fikrin asgari bir uygulamasıdır. Makaledeki retry döngüsüne onu değiştirmeden bağlanır: D'nin bekleme fonksiyonu her retry'dan önce bütçeye sorar, jeton yoksa `RetryBudgetExhaustedException` fırlatır ve bu döngüyü sonlandırır.

### Parametreler ve gerekçeleri

| Değişken | Değer | Neden bu değer |
| --- | --- | --- |
| `E03B_CLIENTS` | 200 | "Çok sayıda istemci": yeniden başlatma anında yaklaşık %70'inin (132-148) bağımlı servisin içinde olmasına yetiyor; bu da yeniden başlayan sürecin bir anda kabul ettiğinden (110) fazla. |
| `E03B_THINK_MIN_MS`, `E03B_THINK_MAX_MS` | 1000-3000 ms, uniform | Ortalama 2 sn, yani saniyede yaklaşık 100 işlem; E03-A ile aynı sunulan yük. Uniform ve sınırlı, böylece hiçbir istemci çok uzun süre boşta kalmıyor. |
| `E03B_DURATION_S` | 30 sn | Donmadan önce 8,5 sn normal çalışma, soğuk başlangıçtan sonra 15 sn. A-D'nin yaklaşık 2,5 dakikada çalışmasına yetecek kadar kısa. |
| `E03B_RESTART_MS` | 10000 | Eşzamanlı arıza 10,0. saniyede, E03-A'nın penceresinin başladığı anda oluyor. |
| `E03B_FREEZE_MS` | 1500 | 2 sn'lik deneme zaman aşımının altında (yukarıya bakın), topluluğun yaklaşık %70'ini toplayacak kadar uzun. |
| `E03B_OUTAGE_MS` | 0 | Yukarıdaki "reddetme dönemi yok" maddesine bakın. 2000 bir duyarlılık çalıştırmasıdır. |
| `E03B_COLD_MS`, `E03B_COLD_SERVICE_TIME_MS` | 5000 ms, 80 ms (125 istek/sn) | E03-A'nın penceresindeki gibi "yarım kapasite", ama enjekte edilen hata yok: E03-B'deki hatalar yalnızca yeniden başlatma ve kabul sınırından geliyor. Sunulan yükün üzerinde, yığılmanın gerektirdiğinin altında. |
| `E03B_DEADLINE_MS` | 5000 | E03-A'da C'nin süre sınırıyla aynı değer, artık her strateji için. Bekletilen bir istek bunun 1,5 sn'sini kullanır. |
| `E03B_BUDGET_RATIO`, `E03B_BUDGET_MAX_TOKENS` | 0,1, 10 | %10 SRE kitabından; 10 jeton gRFC A6'nın örneğindeki gibi. Kova, gruptan çok daha küçük; bu, normal trafiğe göre boyutlandırılan her oranlı bütçenin bir özelliği, bir ayar tercihi değil. |
| `E03B_STRATEGIES` | `A,B,C,D` | Çalıştırma sırası. |
| `E03_WORKERS`, `E03_SERVICE_TIME_MS`, `E03_QUEUE_LIMIT`, `E03_SEED`, `E03_WARMUP_S` | E03-A ile aynı | İki senaryo tarafından paylaşılıyor. |

**Parametreler nasıl seçildi, açıkça.** Yukarıdaki tüm değerler ilk E03-B çalıştırmasından önce belirlendi ve sonra değiştirilmedi. İlk çalıştırma (seed 42) senkronizasyonun amaçlandığı gibi çalıştığını gösterdi, ama bir raporlama sorununu da ortaya çıkardı: istemcinin saati bağımlı servisin sıfırlanmasından birkaç milisaniye sonra başlıyordu. Bu yüzden yeniden başlatma anındaki (10,0 sn) istemci tarafı olaylar 9,9 sn dilimine sayıldı ve D'nin yeniden başlatmadaki 127 hatası 9. saniyeye düştü. Bu da D'nin toparlanma süresini 1 sn yerine 0 sn gösterdi. Düzeltme, E03-B'nin istemci saatini bağımlı servisin saatiyle hizalıyor (sıfırlamanın geçen süresi artı gidiş-dönüş süresinin yarısı; `summary.json` içinde `clientClockOffsetMs`, raporlanan çalıştırmalarda 1,9-14,4 ms). Yalnızca dilimlemeyi değiştiriyor; istemci ve bağımlı servis davranışı ve tüm toplamlar aynı kaldı. Düzeltmeden önceki ilk çalıştırma: etkilenen işlemlerde başarı A %93,5, B ve C %100, D %78,0; 100 ms'deki en yüksek istek 221 / 142 / 69 / 19; istek sayısı 3024 / 2948 / 2956 / 2853; toparlanma 1 / 0 / 0 / 0 sn. E03-A bundan etkilenmedi: saati değişmedi.

### Metrikler (ayrı tutuluyor)

| Metrik | Nerede | Anlamı |
| --- | --- | --- |
| Başarı | `operations.successPercent`, tabloda `Success` | Başlayan mantıksal işlemlerden 200 ile bitenlerin oranı |
| Etkilenen işlemlerde başarı | `byStartPhase.affected`, tabloda `affected` | Donmadan soğuk başlangıcın sonuna kadar (8,5-15 sn) başlayan işlemlerin başarısı |
| Toplam yük | `load.requestsAtDependency`, `load.amplification` | Bağımlı servisin aldığı istekler; mantıksal işlem başına |
| Toparlanma sırasındaki yük | `load.requestsDuringRecovery`, tabloda `10-15 s` | Yeniden başlatmadan soğuk başlangıcın sonuna kadar alınan istekler |
| Anlık yük | `load.peakRequestsPerSecond`, `load.peakRequestsPer100Ms` (+ `AtMs`) | 1 sn'lik ve 100 ms'lik tek bir dilimdeki en yüksek sayı. Senkronize dalgalar bir saniyeden kısadır; saniyelik tepe değer onları gizleyebilir. |
| Eşzamanlılık | `concurrency.heldAtRestart`, `concurrency.peakActiveAfterRestart`, zaman serisinde `inFlight` / `fine.active` | Bağımlı servisin içindeki istekler: yeniden başlatmada kaçının birlikte hata aldığı ve yeniden başlayan süreçteki tepe değer (110 = dolu) |
| Denemeler | `load.attemptsSent`, `load.retriesSent`, `load.retriesDenied`, `operations.attemptsHistogram` | Toplam deneme, retry (2-4. denemeler), bütçenin reddettiği retry'lar |
| Gecikme | `latencyMs.all.p95`, `.p99` | Mantıksal işlem başına uçtan uca: başlangıcından son sonucuna kadar; başarılı ve başarısız **tüm** işlemler (ayrımı `latencyMs.succeeded` / `.failed` içinde) |
| Toparlanma | `recovery.seconds` | Yeniden başlatma saniyesinden (10) sonra, tamamlanan işlemlerin saniyelik başarısının 3 sn boyunca ≥ %95 olmasına kadar geçen saniye (E03-A ile aynı kural) |

### Gözlemlenen sonuçlar (seed 42)

E03-A ile aynı makine (MacBook, Apple Silicon, Docker Desktop 28.4.0 / Compose 2.39.4; Docker'ın gördüğü 12 CPU ve yaklaşık 8 GB), 2026-10-23. `E03_SCENARIOS=b docker compose up --build` çıktısı; varsayılan çalıştırmanın E03-B bölümüyle aynıdır (`results/sample/e03-b/console.txt` içinde de var):

```text
====================================================================================================
E03-B Correlated failure and recovery pressure: same clients, same freeze and restart, 4 client strategies
====================================================================================================
Outcome                           Ops  Success  affected      p95      p99  Recovery
----------------------------------------------------------------------------------------------------
A  immediate retry (UNSAFE)      2820    98.7%     92.9%   628 ms  1600 ms       1 s
B  backoff 200/400/800 ms        2774   100.0%    100.0%   844 ms  2132 ms       0 s
C  backoff + full jitter         2788   100.0%    100.0%   870 ms  2043 ms       0 s
D  C + retry budget              2848    95.6%     78.0%   131 ms  1192 ms       1 s

Load at the dependency         Requests  Ampl.  Retries  Denied  10-15 s  Peak/s Peak/100ms  Active*
----------------------------------------------------------------------------------------------------
A  immediate retry (UNSAFE)        3040  1.08x      220       0      619     275        232      110
B  backoff 200/400/800 ms          2956  1.07x      182       0      537     229        143      110
C  backoff + full jitter           2959  1.06x      171       0      549     225         76      111
D  C + retry budget                2858  1.00x       10     126      440     120         19       19
----------------------------------------------------------------------------------------------------
200 clients, closed loop, think 1-3 s (about 100 ops/s), 30 s, seed 42. Dependency 250 req/s (10 x 40 ms), queue 100.
Freeze 8.5-10.0 s, restart at 10.0 s fails every request inside, cold start until 15.0 s: 125 req/s (80 ms).
All: 4 attempts max, 2 s each, 5 s per op. D: retry budget 0.1 token per new op, at most 10, shared.
affected: success of ops started 8.5-15.0 s. p95/p99: end-to-end latency of all ops, succeeded and failed. Recovery: s after the restart second until per-second success >= 95% for 3 s.
Requests: received by the dependency, first attempts and retries; Ampl.: per logical op; 10-15 s: requests from the restart to the end of the cold start;
Peak/s, Peak/100ms: highest request count in one bucket; Active*: peak requests inside the restarted dependency.
```

**Arıza gerçekten senkronize mi?** Yeniden başlatma çevresinde bağımlı servise 100 ms'de gelen istekler, `results/sample/e03-b/timeseries.json` (`fine`) dosyasından; "rej." = içeride zaten 110 istek olduğu için anında reddedilen; "a2/a3/a4" = istemcilerin o dilimde gönderdiği 2., 3. ve 4. denemeler:

| Strateji | Seri | 9,9 | 10,0 | 10,1 | 10,2 | 10,3 | 10,4 | 10,5 | 10,6 | 10,7 | 10,8 | 10,9 | 11,0 | 11,1 | 11,2 | 11,3 | 11,4 | 11,5 |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| **A** | istek | 11 | 232 | 4 | 5 | 6 | 7 | 8 | 4 | 3 | 5 | 1 | 1 | 8 | 6 | 3 | 8 | 6 |
|  | rej. | 0 | 121 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
|  | a2 | 0 | 144 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
|  | a3 | 0 | 38 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
|  | a4 | 0 | 38 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
|  | içeride | 137 | 137 | 102 | 98 | 93 | 86 | 83 | 73 | 65 | 59 | 53 | 44 | 31 | 27 | 22 | 14 | 11 |
| **B** | istek | 11 | 11 | 3 | 143 | 6 | 11 | 8 | 36 | 4 | 6 | 1 | 0 | 4 | 5 | 2 | 8 | 3 |
|  | rej. | 0 | 0 | 0 | 35 | 0 | 0 | 0 | 9 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
|  | a2 | 0 | 0 | 0 | 138 | 0 | 3 | 0 | 0 | 0 | 2 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
|  | a3 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 32 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
|  | a4 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 7 | 0 |
|  | içeride | 138 | 138 | 8 | 110 | 104 | 103 | 98 | 110 | 102 | 98 | 92 | 83 | 73 | 60 | 54 | 52 | 43 |
| **C** | istek | 10 | 76 | 73 | 19 | 13 | 11 | 15 | 8 | 4 | 4 | 2 | 0 | 4 | 5 | 3 | 1 | 4 |
|  | rej. | 0 | 0 | 19 | 9 | 3 | 0 | 3 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
|  | a2 | 0 | 65 | 67 | 8 | 1 | 1 | 0 | 1 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
|  | a3 | 0 | 0 | 1 | 6 | 6 | 3 | 6 | 1 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
|  | a4 | 0 | 0 | 0 | 0 | 0 | 0 | 1 | 2 | 0 | 0 | 1 | 0 | 0 | 0 | 1 | 0 | 0 |
|  | içeride | 137 | 137 | 110 | 111 | 110 | 110 | 110 | 108 | 101 | 95 | 88 | 71 | 63 | 56 | 49 | 42 | 26 |
| **D** | istek | 11 | 14 | 11 | 4 | 8 | 7 | 8 | 4 | 4 | 4 | 2 | 3 | 12 | 12 | 10 | 9 | 7 |
|  | rej. | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
|  | a2 | 0 | 3 | 7 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
|  | a3 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
|  | a4 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
|  | reddedilen retry | 0 | 126 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
|  | içeride | 136 | 136 | 13 | 11 | 6 | 10 | 8 | 6 | 4 | 4 | 4 | 4 | 12 | 13 | 11 | 10 | 7 |

"içeride", o dilimde bağımlı servisin içindeki en yüksek istek sayısıdır (yeniden başlatmaya kadar eski sürecin beklettiği istekler dahil; 110 = yeniden başlayan süreç dolu).

- **Grup tek bir anda hata aldı.** Bu çalıştırmada 136-138 istek (stratejiye göre) 10,0 sn diliminde hata aldı. 8,0 ile 10,0 sn arasında 100 ms'de 5-14 istek geliyordu.
- **A** bütün retry'larını aynı 100 ms içinde gönderdi: 10,0 sn diliminde 232 istek, bunlardan 121'i anında reddedildi; 38 işlem dört denemesinin hepsini bu 100 ms içinde kullandı ve başarısız oldu (`exhausted_503`). Yeniden başlayan süreç ilk andan itibaren doluydu (içeride 110).
- **B'nin retry'ları tam olarak dalgalar halinde geldi**: +200 ms (10,2 sn: 138 ikinci deneme, 35'i reddedildi), +600 ms (10,6 sn: 32 üçüncü deneme) ve +1400 ms (11,4 sn: 7 dördüncü deneme). Sonraki dalgalar yer buldu ve her işlem başarılı oldu.
- **C aynı retry'ları** 10,0-10,6 sn aralığına yaydı: B'nin 143'üne karşı 100 ms'de en fazla 76 istek. Reddedilen istek sayısı B'ninkinden azdı (34'e karşı 44) ve her işlem başarılı oldu.
- **136 işlem birlikte hata aldığında D'nin bütçesinde 10 jeton vardı.** 10 retry'a izin verdi, 126'sını reddetti; bu 126 işlem hemen başarısız oldu. D en az isteği gönderdi; tepe değerleri (100 ms'de 19, yeniden başlayan süreçte en fazla 19 istek) ve gecikmesi en düşüktü; başarısı da en düşüktü (etkilenen işlemlerin %78,0'i).

### Beş seed ile tekrar

`./repeat-e03.sh` iki senaryoyu 42, 1, 2, 3 ve 4 seed'leriyle çalıştırdı (senaryo başına 5 çalıştırma, her biri bir `docker compose up`, aynı makine, 2026-10-22). Seed, düşünme sürelerini ve jitter değerlerini değiştirir; zaman çizelgesi değişmez. Beş çalıştırma üzerinden medyan [en düşük - en yüksek], `results/sample/repeat.txt` dosyasından:

**Sonuç**

| | Ops | Success | Affected | p95 | p99 | Recovery |
| --- | --- | --- | --- | --- | --- | --- |
| A | 2797 [2778-2817] | %98,6 [98,4-98,7] | %92,7 [91,8-93,3] | 626 [596-664] ms | 1725 [1646-1777] ms | 1 [1-1] sn |
| B | 2759 [2741-2771] | %100,0 [100,0-100,0] | %100,0 [100,0-100,0] | 840 [807-915] ms | 2287 [2192-2319] ms | 0 [0-0] sn |
| C | 2767 [2748-2786] | %100,0 [100,0-100,0] | %100,0 [99,8-100,0] | 892 [843-914] ms | 2030 [1936-2150] ms | 0 [0-0] sn |
| D | 2829 [2814-2845] | %95,3 [95,2-95,5] | %77,8 [76,7-78,1] | 170 [138-202] ms | 1253 [1204-1280] ms | 1 [1-1] sn |

**Bağımlı servisteki yük**

| | Requests | Ampl. | Retries | Denied | 10-15 sn istek | Peak/s | Peak/100 ms | Yeniden başlatma sonrası içeride | Yeniden başlatmada bekleyen |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| A | 3027 [3001-3055] | 1,08 [1,08-1,09] | 224 [222-242] | 0 | 629 [609-632] | 278 [271-289] | 234 [230-247] | 110 [110-110] | 141 [138-148] |
| B | 2950 [2934-2978] | 1,07 [1,07-1,08] | 193 [188-212] | 0 | 549 [543-566] | 230 [228-238] | 146 [144-155] | 110 [110-110] | 141 [139-147] |
| C | 2946 [2922-2991] | 1,06 [1,06-1,07] | 177 [174-205] | 0 | 549 [530-567] | 229 [223-248] | 78 [76-86] | 110 [110-110] | 141 [137-148] |
| D | 2839 [2824-2855] | 1,00 [1,00-1,00] | 10 [10-10] | 131 [128-138] | 441 [436-453] | 120 [117-126] | 20 [18-24] | 23 [17-28] | 141 [138-148] |

Burada seed'ler arasındaki değişim, farkların çoğuna göre küçük; aşağıdaki seed içi karşılaştırmaların hepsi beş çalıştırmada da geçerli oldu:

- **Grup:** Yeniden başlatmada 132-148 istek birlikte hata aldı (örnek çalıştırma dahil tüm E03-B çalıştırmaları). Tekrar çalıştırmalarının her birinde dört stratejinin grupları birbirinden en fazla 2 farklıydı (örnek çalıştırmada: 136-138).
- **100 ms'lik anlık yük:** A 230-247 > B 144-155 > C 76-86 > D 18-24. C'nin 100 ms'lik tepe değeri her çalıştırmada B'ninkinin 0,52-0,55 katıydı.
- **1 sn'lik anlık yük:** B 228-238, C 223-248; aralarında tutarlı bir sıra yok (iki seed'de C daha yüksekti). Saniyelik metrik jitter'ın yarattığı farkı göstermiyor; 100 ms'lik metrik gösteriyor.
- **Toplam yük:** B ile C her çalıştırmada en fazla 13 istek farklıydı, tutarlı bir yönü yok. C her çalıştırmada B'den 7-19 daha az retry gönderdi ve anında daha az reddedildi (47-65'e karşı 33-58). A, B ve C'den yaklaşık %3 daha fazla, D yaklaşık %3-4 daha az istek gönderdi.
- **Başarı:** B %100; C %100, yalnızca iki çalıştırmada birer işlem denemelerini tüketti (etkilenenlerde %99,8). A 36-44 işlem kaybetti; bunların hepsi dört denemesinin hepsini yeniden başlatmadan sonraki 200 ms içinde kullandı (biri dışında hepsi yeniden başlatmanın 100 ms'lik diliminde). D 127-138 işlem kaybetti: bütçede yeniden başlatma anında her seferinde tam 10 jeton vardı; 10 retry'a izin verdi, gerisini reddetti.
- **Gecikme:** p99 her çalıştırmada C'de B'den düşüktü (91-351 ms); p95 dört çalıştırmada B'de C'den düşüktü, beşincisinde 1 ms içinde eşitti. En düşük gecikme açık farkla D'deydi, çünkü başarısız işlemleri hemen bitti.
- **Toparlanma:** Her çalıştırmada B ve C için 0 sn, A ve D için 1 sn (ikisinde de yeniden başlatma saniyesinde hatalar vardı).
- Her E03-B çalıştırmasında: hiçbir deneme zaman aşımına uğramadı, hiçbir istek terk edilmedi, süre sınırı hiçbir işlemi kesmedi. Yeniden başlatma 10,0 sn'den 0-5,8 ms sonra tetiklendi; istemci saati farkı 1,9-14,4 ms idi; istemcilerin en geç uyanma gecikmesi 6-34 ms, bir kez 129 ms oldu (B, seed 42 tekrar çalıştırması; sayıları diğer çalıştırmalarla uyumlu).

### Duyarlılık çalıştırmaları

Her varyant bir kez çalıştırıldı (seed 42, `E03_SCENARIOS=b`, diğer her şey varsayılan). Bunlar tek çalıştırmalardır: bir aralık değil, bir yön gösterirler. Konsol çıktıları: `results/sample/e03-b-sensitivity/`.

| Varyant | Komut | Etkilenenlerde başarı A / B / C / D | İstek A / B / C / D | Peak/s A / B / C / D | Peak/100 ms A / B / C / D | Toparlanma A / B / C / D |
| --- | --- | --- | --- | --- | --- | --- |
| Ana yapılandırma (5 çalıştırmanın medyanı) | `docker compose up` | %92,7 / 100,0 / 100,0 / 77,8 | 3027 / 2950 / 2946 / 2839 | 278 / 230 / 229 / 120 | 234 / 146 / 78 / 20 | 1 / 0 / 0 / 1 sn |
| Yeniden başlatmadan sonra 2 sn outage | `E03B_OUTAGE_MS=2000` | %63,6 / 71,9 / 71,9 / 63,6 | 3697 / 3308 / 3426 / 2873 | 631 / 403 / 559 / 120 | 463 / 157 / 105 / 18 | 2 / 2 / 2 / 2 sn |
| Küçük kuyruk (en fazla 30 içeride) | `E03_QUEUE_LIMIT=20` | %79,9 / 86,9 / 94,8 / 78,1 | 3218 / 3113 / 3168 / 2858 | 429 / 305 / 406 / 120 | 384 / 140 / 92 / 18 | 1 / 2 / 1 / 1 sn |
| Soğuk başlangıcın taşıyabileceğinden fazla yük | `E03B_CLIENTS=300` (saniyede yaklaşık 150 işlem) | %85,7 / 94,5 / 95,0 / 76,2 | 4571 / 4473 / 4545 / 4191 | 518 / 398 / 523 / 176 | 450 / 217 / 129 / 24 | 1 / 2 / 1 / 1 sn |

- **2 sn outage.** Yeniden başlayan süreç 12,0 sn'ye kadar her isteği reddettiği için, grubun retry'ları her stratejide outage'ın içine düştü (B'nin +1,4 sn'deki son dalgası dahil). Başarıyı bekleme değil outage belirledi: A, B ve C'de 178-283 işlem denemelerini reddedilmelerle tüketti. Backoff burada yükü azalttı: A 3697 istek (1,30x) ve ilk 100 ms'sinde 463 istek gönderdi, B 3308 (1,22x). C, B'den fazla istek gönderdi (3426) ve saniyedeki tepe değeri daha yüksekti (403'e karşı 559): ortalama beklemesi daha kısa olduğu için her istemci başarısız bir işlemi daha erken bitirip bir sonrakine daha erken başladı (closed loop) ve outage sırasında daha sık denedi. D, A ile aynı oranda etkilenen işlemi kaybetti (%63,6); A'nın 849 retry'ına karşı 24 retry ile.
- **Küçük kuyruk.** Grup (137), kabul sınırının (30) yaklaşık 4,5 katıydı. B'nin senkronize dalgaları (+200, +600, +1400 ms'de 100 ms'de 140, 115, 93 istek) her seferinde sınırı aştı ve 65 işlem dört denemesinin hepsini tüketti. C aynı retry'ları yaydı ve 30 işlem kaybetti. A 115 işlem kaybetti. Burada jitter yalnızca yükün biçimini değil sonucu da değiştirdi; B'nin toparlanması (2 sn) ve p99'u (2,5 sn) da en uzun olanıydı.
- **300 istemci.** Grup 207-210 istekti ve sunulan yük (saniyede yaklaşık 150 işlem) 5 sn boyunca soğuk başlangıç kapasitesini (saniyede 125 istek) aştı; yani bağımlı servis retry'lardan bağımsız olarak aşırı yüklüydü. B ve C hemen hemen aynı başarıyla bitirdi (38 ve 34 işlem kaybı); C yine B'den fazla istek ve retry gönderdi ve saniyedeki tepe değeri daha yüksekti; 100 ms'deki tepe değer ise B'de daha yüksekti.

### E03-B ne gösteriyor

1. **Eşzamanlı bir arıza, sabit backoff'u saniyenin altındaki dalgalara dönüştürür:** arızadan tam olarak +200, +600 ve +1400 ms sonra. Saniyelik metrikler bunları gizler (B ve C'nin saniyedeki tepe değeri aynıydı); 100 ms'lik dilimler gösterir (her çalıştırmada B'nin tepe değeri C'ninkinin yaklaşık iki katıydı).
2. **Dalgaların zarar verip vermemesi kapasite sınırlarına bağlıdır.** Ana yapılandırmada ilk dalga kabul sınırını yaklaşık 30 istekle aştı, sonraki dalgalar yer buldu; yani B senkronizasyon yüzünden bir şey kaybetmedi. Jitter yükün biçimini (100 ms'deki tepe değer, reddedilmeler, p99) değiştirdi; başarıyı ya da toplamı değil. Her dalganın kat kat taştığı kadar küçük bir kuyrukla, B, C'nin iki katından fazla işlem kaybetti (tek çalıştırma).
3. **Full jitter retry'ları dağıtır; geciktirmez.** Ortalama beklemesi B'ninkinin yarısıdır. İstemcilerin bir outage ya da aşırı yüklenmiş bir soğuk başlangıçla sınırlandığı durumlarda C, B'den daha fazla istek gönderdi ve saniyedeki tepe değeri daha yüksekti.
4. **Anında yeniden deneme bütün retry hakkını tek bir ana yığar:** ilk 100 ms'de 230-247 istek (normal hızın yaklaşık 20 katı), en fazla 110 istek kabul eden yeniden başlamış bir sürece karşı; o an içinde içeri giremeyen her işlem başarısız olur.
5. **Orana dayalı bir retry bütçesi retry trafiğini tasarım gereği sınırlar ve bunun bedelini başarısız işlemlerle öder.** Normal trafiğe göre boyutlandırıldığında (%10 artı 10 jeton) yaklaşık 140 eşzamanlı hatanın 10'unu karşıladı. D her çalıştırmada en düşük yükü, tepe değerleri ve gecikmeyi verdi; en düşük başarıyı da. Bunun doğru takas olup olmadığı, başarısız işlemlerin daha sonra kullanıcı ya da üst bir katman tarafından zararsızca yeniden denenip denenemeyeceğine bağlıdır; E03-B bunu modellemiyor.
6. **Toparlanma süresi (1 sn çözünürlük) stratejileri burada pek ayırmıyor;** başarı, yük ve 100 ms'lik seriler ayırıyor.

### E03-B ne göstermiyor

- **Bağımlı servisin kuyruğa alabileceğinden çok daha büyük, çok sayıda bağımsız süreçten gelen bir yığılma.** Burada tek bir yük üretici 200 istemciyi oynuyor ve yığılma (132-148) kabul sınırının (110) biraz üzerinde. Her birinin kendi küçük bütçesi ve zamanlayıcıları olan binlerce instance'tan oluşan bir filo ve kabul sınırının kat kat üzerindeki yığılmalar farklı davranabilir. Küçük kuyruklu duyarlılık çalıştırması bu yöne işaret ediyor; tek bir çalıştırma.
- **Bağlantı düzeyindeki etkiler.** Yeniden başlatma bağlantıları düşürmek yerine 503 döndürüyor; yeniden bağlanma fırtınası, TLS el sıkışması ya da SYN backlog yok.
- **Gerçek ısınma davranışı.** Soğuk başlangıç, yarım hızda sabit 5 sn; gerçek ısınma yüke, cache'lere ve JIT'e bağlıdır.
- **Circuit breaker, adaptive throttling, `Retry-After`, sunucu tarafında önceliğe göre kabul kontrolü.** Hiçbiri uygulanmadı.
- **İstatistiksel anlamlılık.** Tek makinede beş seed dağılımı gösterir; bir güven aralığı değildir.

## Çıktılar ve nasıl okunur

Tüm dosyalar `results/` klasörüne yazılır (bind mount edildiği için host'ta görünür): `results/e03-a/` ve `results/e03-b/`, her birinde `summary.json`, `timeseries.json` ve `console.txt` (o senaryonun konsol çıktısı, yük üreticinin kendisi yazar), ayrıca `./repeat-e03.sh` için `results/repeat/`. `results/` git tarafından yok sayılır; `results/sample/` hariç: `e03-a/` (yukarıda gösterilen E03-A çalıştırması), `e03-b/` (yukarıda gösterilen seed 42 E03-B çalıştırması), `repeat.txt` (beş seed'lik tekrar) ve `e03-b-sensitivity/` (tek çalıştırmalık üç varyantın konsol çıktısı).

### E03-A dosyaları (şema v1)


#### Konsol özeti (Şekil 2)

| Sütun | Anlamı |
| --- | --- |
| Requests | Ölçülen çalıştırma boyunca **bağımlı servisin aldığı** istekler (kabul edilen + reddedilen) |
| Ampl. | İstek / mantıksal işlem (3038). 1,00x hiç retry olmadığı anlamına gelir. |
| Success | `200` ile biten mantıksal işlemler |
| in window | **Planlanan başlangıcı** arıza penceresinin (10-20 sn) içinde olan işlemlerin başarı oranı |
| p95, p99 | **Tüm** mantıksal işlemlerin (başarılı ve başarısız) planlanan gelişten nihai sonuca kadar uçtan uca gecikmesi (nearest-rank yüzdelik) |
| Peak rps | Bağımlı serviste bir saniyede görülen en yüksek istek sayısı |
| Recovery | Pencere bittikten sonra, saniyelik başarı oranı art arda 3 saniye ≥ %95 olana kadar geçen saniye (tanım aşağıda) |

**Toparlanma süresinin kesin tanımı.** Tamamlanan mantıksal işlemler, **tamamlandıkları** saniyeye göre gruplanır. Toparlanma, *s*, *s+1* ve *s+2* saniyelerinin her birinde en az bir tamamlanma olduğu ve `succeeded / (succeeded + failed) ≥ 0,95` koşulunun sağlandığı ilk *s* ≥ 20 saniyesidir. Toparlanma süresi = *s* − 20, tam saniye olarak. Yani `0 s`, pencereden sonraki ilk saniyenin koşulu zaten sağladığı anlamına gelir. Yalnızca yük durmadan önceki saniyeler (≤ 29) dikkate alınır. Bu aralığa sığan bir seri yoksa tabloda `>7 s` görünür.

#### `summary.json`

Önce çalıştırma bilgileri gelir (`startedAtUtc`, runtime, imajlar, CPU sayısı, tüm parametreler, toparlanma tanımı), ardından strateji başına bir nesne:

| Alan | İçerik |
| --- | --- |
| `operations` | `started`, `succeeded`, `failed`, `successPercent`, `failedBy` (`exhausted_503`, `exhausted_timeout`, `exhausted_http_error`, `deadline_exceeded`), `attemptsHistogram` (kullandığı deneme sayısına göre işlemler) |
| `attempts` | `sentByClient`, `receivedByDependency`, `amplificationClient`, `amplificationDependency`, `responsesByStatus`, `timeouts`, `cutByDeadline`, `httpErrors` |
| `dependency` | Bağımlı servis tarafı toplamları: `received`, `ok`, `injected503`, `rejected503`, `abandoned`, `peakInFlight`, `peakQueued`, `peakReceivedPerSecond` |
| `latencyMs` | `all`, `succeeded` ve `failed` işlemler için `p50`, `p95`, `p99`, `max` |
| `byArrivalPhase` | Planlanan başlangıca göre gruplanan işlemler: `beforeWindow`, `inWindow`, `afterWindow`; her biri için başarı yüzdesi ve işlem başına deneme |
| `successPercentByArrivalSecond` | 0-29 arasındaki her saniyede planlanan işlemlerin başarı yüzdesi |
| `recovery` | `recovered`, `seconds`, `firstHealthySecond` |
| `maxDispatchLagMs` | Yük üreticinin bir işlemi takvimine göre en fazla ne kadar geç başlattığı (gecikmeye dahildir) |

#### `timeseries.json` (Şekil 1)

`failureWindow: {startS: 10, endS: 20}`, `arrivalsEndS: 30`, her alanı açıklayan bir `fields` sözlüğü ve strateji başına saniyede bir nokta (bu örnek, örnek çalıştırmada A stratejisinin 15. saniyesi):

```json
{ "t": 15, "dependencyRequests": 311, "succeeded": 61, "failed": 51, "inFlight": 110,
  "started": 109, "attemptsSent": 311, "rejected503": 195, "injected503": 59, "queued": 100 }
```

- `t`: çalıştırma başından itibaren saniye ([t, t+1) aralığı).
- `dependencyRequests`: o saniyede bağımlı servisin aldığı istekler (bağımlı serviste ölçülür). Şekil 1'de her strateji için bir çizgi olarak çizilen seri budur.
- `succeeded`, `failed`: o saniyede **tamamlanan** mantıksal işlemler (istemci tarafı).
- `inFlight`: o saniye içinde bağımlı servisin içindeki en yüksek istek sayısı (işlenen + kuyrukta bekleyen). 110 = 10 worker + dolu kuyruk.
- Ek alanlar: `started` (sunulan yük, her stratejide aynı; grafikte işe yarar bir referans çizgisi), `attemptsSent` (istemci tarafı çapraz kontrol), `rejected503`, `injected503`, `queued`.

30. saniyeden sonraki saniyelerde yalnızca boşalma görülür (30. saniyeden önce başlayıp hâlâ yeniden denenen işlemler). Grafik için CSV:

```bash
jq -r '.strategies[] | .id as $s | .points[] | [$s, .t, .dependencyRequests, .started] | @csv' results/e03-a/timeseries.json
```

#### Loglar

Bağımlı servis her satıra bir JSON nesnesi yazar (`docker compose logs --no-log-prefix dependency`). Özetin okunaklı kalması için konsola bağlanmaz. Alanlar: `ts`, `level`, `phase` (`startup`, `run_reset`, `failure_window_started`, `failure_window_ended`, `freeze_started`, `restarted`, `outage_ended`, `cold_ended`, `shutdown`), `run_id` (`A`, `B`, `C`, `warmup-A`, `b-A` ... `b-D`, `warmup-b-A`, ...), ayrıca `startup` satırında yapılandırma ve pencere sınırlarında `in_system`:

```json
{"ts":"2026-10-22T09:54:15.582Z","level":"info","category":"Dependency","run_id":"A","phase":"failure_window_ended","t_s":20,"in_system":110,"detail":"Recovered: service time 40 ms, no injected errors","msg":"phase=failure_window_ended t_s=20 in_system=110 Recovered: service time 40 ms, no injected errors"}
```

Her deneme ayrıca `X-Operation-Id` ve `X-Attempt` istek başlıklarını taşır. `E03_ATTEMPT_LOG=true` ile yük üretici bunları her denemenin sonucuyla birlikte `results/e03-x/attempts.jsonl` dosyasına yazar.

### E03-B dosyaları (şema v2)

`results/e03-b/` aynı üç dosyayı içerir. E03-A şema v1'de, değişmeden kalıyor; böylece commit edilmiş örneği hâlâ mevcut kodun yazdığıyla aynı. Alanların farklı olduğu yerde v2 kullanılıyor.

**`summary.json`** (`drlab-e03/summary/v2`): `scenario`, v1'deki gibi çalıştırma meta verisi, `parameters` (closed-loop ayarlarıyla `load`, `dependency`, ms cinsinden zaman çizelgesiyle `failure`, her stratejinin süre sınırını ve `retryBudget` kuralını içeren `client`, `recovery`, `affectedOperations`, `requestsDuringRecovery`), ardından strateji başına bir nesne:

| Alan | İçerik |
| --- | --- |
| `retryBudget` | Yalnızca D: `ratio`, `maxTokens`, `firstAttempts`, `granted`, `denied`, `tokensLeft` |
| `operations` | v1'deki gibi; ayrıca `failedBy` içinde `retry_budget_exhausted` sonucu |
| `byStartPhase` | İşlemin başladığı ana göre `beforeFreeze`, `affected` (8,5-15 sn), `afterColdStart` |
| `load` | `requestsAtDependency`, `amplification`, `attemptsSent`, `firstAttempts`, `retriesSent`, `retriesDenied`, `requestsDuringRecovery`, `peakRequestsPerSecond`, `peakRequestsPer100Ms`, `peakRequestsPer100MsAtMs`, `responsesByStatus`, `timeouts`, `cutByDeadline`, `httpErrors` |
| `concurrency` | `peakActive` (donmanın beklettiği istekler dahil), `heldAtRestart`, `peakActiveAfterRestart` |
| `dependency` | `received`, `ok`, `rejected503`, `restart503`, `refused503`, `abandoned`, `restartedAtMs` (yeniden başlatma zamanlayıcısının gerçekte tetiklendiği an, 0-5 ms gecikmeli) |
| `latencyMs`, `recovery` | v1'deki gibi; toparlanma yeniden başlatma saniyesinden itibaren sayılır |
| `maxWakeLagMs`, `clientClockOffsetMs` | Bir istemcinin planına göre ne kadar geç uyandığı; istemci saatini bağımlı servisinkiyle hizalayan fark |

**`timeseries.json`** (`drlab-e03/timeseries/v2`): `failure` (`freezeStartMs`, `restartMs`, `outageEndMs`, `coldEndMs` ve bir `phases` listesi), her alanı açıklayan `fields` ve `fineFields`, ve strateji başına:

- `points`, saniyede bir, v1'deki gibi (`t`, `dependencyRequests`, `succeeded`, `failed`, `inFlight`, `started`, `attemptsSent`, `rejected503`, `queued`) ve ek olarak `retriesSent`, `retriesDenied`, `restart503`, `refused503`;
- `fine`, dizi biçiminde 100 ms'lik dilimler (*i*. eleman [*i* x 100, (*i* + 1) x 100) ms aralığını kapsar): `dependencyRequests`, `active`, `queued`, `ok`, `rejected503`, `restart503`, `refused503`, `attempt1` ... `attempt4`, `retriesDenied`, `succeeded`, `failed`.

```bash
# yeniden başlatma çevresinde 100 ms'deki istekler, strateji başına bir satır
jq -r '.strategies[] | [.id, (.fine.dependencyRequests[95:120] | map(tostring) | join(" "))] | join(": ")' results/e03-b/timeseries.json
```

Bağımlı servis `freeze_started`, `restarted` (`held`: yeniden başlatmada hata alan istek sayısı, ve `actual_ms`), `outage_ended` (yalnızca outage varsa) ve `cold_ended` satırlarını loglar.

## Birden fazla seed ile tekrarlama

```bash
# E03-A ve E03-B, seed 42 1 2 3 4: yaklaşık 21 dakika
./repeat-e03.sh
E03_REPEAT_SEEDS="7 8 9" ./repeat-e03.sh
# yalnızca E03-B
E03_SCENARIOS=b ./repeat-e03.sh
```

Betik `results/repeat/` klasörünü boşaltır, her seed için bir kez `docker compose up` çalıştırır (çıktı `results/repeat/seed-N/` ve `results/repeat/seed-N.log` içinde), ardından `LoadGenerator aggregate` çalıştırır. Bu komut her `summary.json` dosyasını okur; senaryo, strateji ve metrik başına medyanı ve en düşük-en yüksek aralığını, ardından seed sırasıyla tek tek her değeri yazdırır ve `results/repeat/repeat.txt` ile `repeat.json` dosyalarını yazar. Diğer tüm `E03_*` / `E03B_*` değişkenleri olduğu gibi aktarılır. macOS'ta çalıştığı süre boyunca makineyi `caffeinate` ile uyanık tutar.

Beş çalıştırma üzerinden medyan ve aralıklar dağılımın bir tarifidir, istatistiksel bir test değildir. Aynı seed içinde stratejiler aynı düşünme süresi ve jitter dizilerini görür; bu yüzden stratejileri aynı seed içinde karşılaştırmak (seed başına değerler), aralıkları karşılaştırmaktan daha bilgilendiricidir.

## Otomatik testler

`tests/RetryResilience.Tests` (xUnit v3, 105 test; ağ ya da konteyner gerekmez; 55'i E03-A'dan, 50'si E03-B ile eklendi):

| Alan | Neler doğrulanıyor |
| --- | --- |
| Bekleme takvimleri | A hiç beklemez. B tam olarak 200/400/800 ms bekler (ötesinde de makaledeki üst sınırı izler). C, makaledeki 200, 400, 800, 1600, 2000, 2000 üst sınırları için [0, üst sınır] aralığında kalır, iki uca da ulaşır, ortalaması yaklaşık üst sınır/2'dir ve işlem bazında tekrarlanabilir. |
| Geçici hata sınıflandırması | 408/429/502/503/504 yeniden denenir. 200/400/404/409/500/501 denenmez. |
| Retry döngüsü (senaryolu handler) | 503'ü başarıya kadar yeniden dener. 4 denemeden sonra durur. 500/404'ü yeniden denemez. `HttpRequestException`'ı yeniden dener ve 4. denemede yeniden fırlatır. Takılan bir deneme 2 sn sonra iptal edilip yeniden denenir. B'nin denemeleri arasındaki boşluklar en az 200/400/800 ms'dir. İşlem süre sınırı, bekleyen bir beklemeyi ve devam eden bir denemeyi iptal eder. |
| Retry bütçesi | Dolu başlar ve tam `maxTokens` kadar retry'a izin verir, sonra reddeder. 0,1 oranında on yeni işlem bir retry'ı karşılar (ikili yuvarlamaya rağmen). Jeton sayısı üst sınırı asla aşmaz. Rastgele 100.000 adımlık bir dizide izin verilen retry'lar hiçbir zaman 10 + 0,1 x işlem sayısını aşmaz. Sekiz eşzamanlı çağıran bütçeyi aşmaz. Geçersiz parametreler reddedilir. |
| D stratejisi | Jeton olduğu sürece aynı işlem için C ile aynı beklemeleri kullanır. Kova boşken beklemek yerine exception fırlatır. Tek jetonla makaledeki döngü iki deneme gönderip durur. Reddedilen bir retry işlemi `retry_budget_exhausted` olarak bitirir ve sayılır. E03-B'de tüm stratejiler 5 sn'lik süre sınırını paylaşır ve yalnızca D'nin bütçesi vardır; E03-A'nın A, B, C'si değişmedi. |
| E03-B arıza modeli | Zaman çizelgesi sınırları (8,5 sn'de donma, 10 sn'de yeniden başlatma, 15 sn'ye kadar soğuk başlangıç; outage ile ve outage olmadan). Soğuk servis süresi yalnızca soğuk başlangıçta. Varsayılan donma 2 sn'lik deneme zaman aşımından kısa. Geçersiz zaman çizelgeleri reddedilir. Doğrudan bağımlı servisin istek yolu üzerinden: yeniden başlatma anında içerideki her istek (serviste, kuyrukta ve kabul sınırının ötesinde) yeniden başlatmadan sonraki birkaç ms içinde 503 alır; donmadan önce normal hizmet verir ve yük atar; yeniden başlatmadan sonra yeni bir süreç worker + kuyruk kadar istek kabul eder ve soğuk hızda hizmet verir; outage sırasında her istek reddedilir; E03-A'nın pencere modu donma ayarlarını yok sayar. |
| İnce taneli dilimler | Bağımlı servis ve istemci milisaniyeleri aynı 100 ms'lik dilimlere eşler (sınırlar, kırpma). On adet 100 ms'lik dilim kendi saniyesine eşit toplanır. İstemci, hizalanmış saatte denemeleri 100 ms'lik dilimlerde deneme numarasına göre sayar. Tepe değer ve aralık toplamı yardımcıları. |
| Tekrar toplama | Medyan (tek, çift, tek eleman, boş). En düşük/en yüksek/medyan eksik değerleri yok sayar ve sayar. Noktalı JSON yolları. Bir klasörden `seed-N/e03-b/summary.json` okuma: sayısal seed sırası, strateji başına değerler, aralıklar, biçimlenmiş hücreler, eksik senaryo. |
| Düşünme süreleri | Aynı seed ve istemci aynı diziyi verir, sınırlar içinde kalır; ilk başlangıçlar [0, max) aralığına yayılır. |
| Metrikler | Nearest-rank yüzdelikler (1..100, küçük listeler, boş liste, geçersiz p). Toparlanma süresi: anlık, gecikmeli seri, tam %95 ile %94,9, tamamlanma olmayan saniye, yük durmadan önce sığmayan seri. |
| İş yükü ve bağımlı servis | Aynı seed aynı geliş takvimini verir. Gelişler artan sıradadır ve hıza yakındır. Kabul sınırı worker + kuyruktur. Worker'lar FIFO'dur. Kapasite hesabı doğrudur. |

```bash
# Docker içinde, yerel .NET SDK gerekmez
./test-e03.sh
# betik olmadan eşdeğeri
docker compose run --rm --build tests
```

2026-10-22 tarihli çıktı (kısaltılmış):

```text
  Passed RetryResilience.Tests.CorrelatedWorkTests.The_E03_A_window_mode_is_not_affected_by_the_freeze_settings [289 ms]
  Passed RetryResilience.Tests.RetryLoopTests.An_attempt_that_exceeds_2_s_is_cancelled_and_retried [2 s]
  ...
Test Run Successful.
Total tests: 105
     Passed: 105
```

Testlerin gerçekten ayırt edici olduğunu doğrulamak için paket, kasıtlı olarak bozulmuş varyantlara karşı da çalıştırıldı. E03-A için (`RetryLoop.cs`): üst sınıra hiç ulaşmayan jitter (`Next(0, capMs)`) 6 testi, geçici sayılan 500 2 testi, 4 yerine 5 deneme 5 testi düşürdü. Retry bütçesi için: bir jetondan az varken retry'a izin vermek 4 testi, üst sınırı kaldırmak 1 testi, bütçeyi yok sayan D 3 testi, `TryRetry` içindeki kilidi kaldırmak 1 testi (eşzamanlılık testi; bir yarış durumu olduğu için her seferinde başarısız olmayabilir) düşürdü. Her varyant geri alındı ve paket yeniden geçti.

**Testler neyi garanti eder, neyi etmez?** Testler süreç içinde çalışır: konteyner, ağ ya da gerçek zaman baskısı yoktur (2 sn'lik bir timeout testi hariç). Aşağıda sabitlenen .NET sürümüyle çalıştırılmıştır. Laboratuvarın yapı taşlarının belirtildiği gibi davrandığını doğrularlar: bekleme takvimleri, geçici hata sınıflandırması, retry döngüsünün sınırları, retry bütçesi, arıza zaman çizelgesi, zaman dilimlerine ayırma ve toplulaştırma. Deneyin sonuçlarını **doğrulamazlar**. Hiçbir test bir stratejinin diğerinden iyi olduğunu, bu README'deki bir sayının yeniden üretildiğini ya da bağımlı servis modelinin gerçek bir servise benzediğini iddia etmez. Sonuçlar yalnızca deneyi çalıştırarak elde edilir ve makineye, parametrelere ve seed'e bağlıdır (bkz. Sınırlamalar). Testlerin geçmesi üretim kullanımı hakkında da bir şey söylemez: bu kod bir laboratuvardır, kütüphane değildir.

## Klasör yapısı

```text
experiments/retry-resilience/
├── compose.yaml              # "drlab-e03" projesi: `docker compose up --build` iki senaryoyu çalıştırır; parametreler; test profili
├── Dockerfile                # build / test / runtime aşamaları; iki programı içeren tek imaj
├── test-e03.sh               # xUnit paketini Docker içinde çalıştırır, ardından temizler
├── repeat-e03.sh             # iki senaryoyu birden fazla seed ile çalıştırır, ardından medyan ve en düşük-en yüksek
├── RetryResilience.slnx
├── src/Dependency/           # simüle bağımlı servis
│   ├── Program.cs            # GET /work, /health, /admin/{reset,stats,shutdown}; yeniden başlatma zamanlayıcısı
│   ├── WorkProcessor.cs      # istek yolu: E03-A penceresi, E03-B donma / yeniden başlatma / soğuk başlangıç
│   ├── CorrelatedFailure.cs  # E03-B zaman çizelgesi
│   ├── CapacityModel.cs      # worker'lar + sınırlı kuyruk, kabul
│   ├── ServerRun.cs          # son sıfırlamadan beri saniyelik ve 100 ms'lik sayaçlar
│   └── DependencyOptions.cs
├── src/LoadGenerator/        # yük üretici ve rapor
│   ├── RetryLoop.cs          # makaledeki retry döngüsü + Backoff (A, B, C)
│   ├── Strategies.cs         # A, B, C, D tanımları
│   ├── RetryBudget.cs        # D stratejisinin token bucket'ı
│   ├── StrategyRunner.cs     # E03-A: ısınma, sıfırlama, open-loop gönderim, boşaltma
│   ├── ClosedLoopRunner.cs   # E03-B: istemci topluluğu
│   ├── RunRecorder.cs        # istemci tarafı sayaçlar (1 sn ve 100 ms), denemeleri sayan handler
│   ├── Metrics.cs            # yüzdelik, toparlanma süresi, tepe değerler
│   ├── Report.cs             # E03-A: summary.json, timeseries.json (v1), konsol tablosu
│   ├── CorrelatedReport.cs   # E03-B: summary.json, timeseries.json (v2), konsol tabloları
│   ├── Repeat.cs             # `aggregate`: seed'ler üzerinden medyan ve en düşük-en yüksek
│   └── ...
├── tests/RetryResilience.Tests/
└── results/                  # çıktı (git yok sayar) + sample/ (commit edilmiş çalıştırmalar)
```

## Sürümler ve ön koşullar

| Bileşen | Sürüm |
| --- | --- |
| .NET SDK / ASP.NET Core runtime imajları | `mcr.microsoft.com/dotnet/sdk:10.0.402`, `mcr.microsoft.com/dotnet/aspnet:10.0.13` (raporlanan runtime: .NET 10.0.13) |
| NuGet (yalnızca testler) | xunit.v3 3.2.2, xunit.runner.visualstudio 3.1.5, Microsoft.NET.Test.Sdk 18.11.0 |

- Compose v2 içeren Docker. `attach: false` için Compose 2.20 veya üstü gerekir. macOS (arm64) üzerinde Docker 28.4 / Compose 2.39 ile denendi.
- Yerel .NET SDK gerekmez.
- Boş loopback portu: `127.0.0.1:18490` (çalışma sırasında `curl` ile bağımlı servise bakmak için). `DRLAB_E03_DEPENDENCY_PORT` ile değiştirilebilir. Yük üretici port açmaz.
- Yük üretici, Linux host'larda bind mount edilen `results/` klasörüne yazabilmek için konteynerinin içinde root olarak çalışır. Oluşturduğu alt klasörler (`e03-a/`, `e03-b/`, `repeat/...`) herkese yazılabilir yapılır; böylece içlerindeki dosyaları root olmadan da silebilirsiniz.

`drlab-e03` compose proje adı, konteynerleri ve ağı E01'den ve makinedeki diğer yerel ortamlardan ayrı tutar.

## Sınırlamalar (sonuçları alıntılamadan önce okuyun)

- **Tek makine, tek sentetik bağımlı servis.** Bağımlı servisin kapasitesi bir zamanlayıcı modelidir: CPU rekabeti, çöp toplayıcı baskısı, bağlantı sınırları ya da alt bağımlılıklar yoktur. Gecikmelere tek host üzerindeki Docker ağı da dahildir. Sıralama burada kararlıydı; mutlak sayılar başka ortamlarda farklı çıkacaktır.
- **Jitter'ın her zaman daha iyi ya da daha kötü olduğu iddia edilmiyor.** E03-A'da hatalar senkronize değil; dağıtılacak pek bir şey yok. E03-B tek bir eşzamanlı arızayı tek bir parametre setiyle (ve tek çalıştırmalık dört varyantla) dener; jitter'ın orada neyi değiştirdiğini gösterir, genel olarak değil.
- **Backoff tek başına retry trafiğini sınırlamaz.** E03-A'da retry bütçesi uygulanmaz; E03-B'de yalnızca D stratejisinin bütçesi var. Circuit breaker ve istemci tarafında uyarlanabilir kısıtlama (adaptive throttling) **uygulanmadı**.
- **Tek istemci süreci.** E03-A open loop; E03-B 200 istemcilik bir closed loop, ama tek bir süreçte ve tek bir ortak bütçeyle. Gerçek çağıranlar, her birinin kendi zamanlayıcıları ve bütçesi olan birçok sürece dağılmıştır.
- **`Retry-After` gönderilmiyor ve dikkate alınmıyor.** Makaledeki kod bu başlığı yok saydığı için bağımlı servis de göndermiyor.
- **Arıza penceresinin kenarları.** 10 sn'lik pencere, beklemelerden (≤ 1,4 sn) çok daha uzun; bu süregiden bir olay. Backoff'tan kısa arızalarda uzun beklemeler daha çok işe yarardı ve A daha kötü görünürdü.
- **Toparlanma süresinin çözünürlüğü 1 sn'dir** ve yukarıdaki tanıma bağlıdır. Yük atan bir bağımlı servis, sebep ortadan kalkınca hemen toparlanır. E03-B'de 100 ms'lik seriler, 1 sn'lik metriklerin gizlediğini gösterir.
- **Yük üretici ile bağımlı servis aynı makineyi paylaşıyor.** E03-A her strateji için en yüksek gönderim gecikmesini kaydeder (her çalıştırmada 6-46 ms; bir kez 120 ms: örnek çalıştırmadaki A stratejisi); gecikme planlanan geliş anından ölçüldüğü için bu gecikme gizlenmez, ölçüme dahildir. E03-B bir istemcinin en geç uyanmasını (`maxWakeLagMs`) ve yeniden başlatma zamanlayıcısının gerçek anını (`restartedAtMs`, 10,0 sn'den 0-5 ms sonra) kaydeder.
- Yönetim uç noktalarında kimlik doğrulama yoktur. A stratejisi bilerek güvensizdir. İkisi de yalnızca yerel kullanım içindir.

## Kısa arıza analizi

**E03-A.** Bağımlı servis, bozulmuş halinde bile asıl yükü taşıyabiliyordu (%80 kullanım). Yanıtlarının yarısı hataydı ve her hata bir istek daha demekti. Retry'lar talebi düşen kapasitenin üzerine çıkardı, kuyruk doldu ve servis istekleri reddetmeye başladı; bu da daha fazla retry'a yol açtı. Her işlemin deneme hakkı aynı olduğundan ve olay her backoff süresinden uzun sürdüğünden, üç strateji bağımlı servise yaklaşık aynı sayıda istek gönderdi. Backoff yalnızca bu isteklerin *ne zaman* geldiğini değiştirdi. Bu zamanlama kenarlarda yine de önemliydi. Anında yeniden denemeler aynı dolu kuyruğa çarpıp en hızlı başarısız oldu. Daha uzun sabit beklemeler bazı işlemlerin olayı atlatmasını sağladı. Daha kısa rastgele beklemeler daha düşük kuyruk gecikmesi verdi. Retry trafiğinin *miktarını* sınırlamak için tek bir isteğin ötesine bakan bir mekanizma gerekir: retry bütçesi, circuit breaker ya da yükü gözeten kısıtlama. Yük atmayan derin bir kuyruk aynı fırtınayı daha da kötüleştirir: iş, çoktan gitmiş istemciler için sürer.

**E03-B.**
Donma yaklaşık 140 isteği bekletti ve yeniden başlatma hepsini tek bir anda hataya düşürdü. Yeniden başlayan sürece bundan sonra neyin ulaşacağını istemcilerin bekleme kuralı belirledi. Anında yeniden deneme, grubun tamamını ve retry'larını milisaniyeler içinde en fazla 110 istek kabul eden bir sürece gönderdi; taşan kısım denemelerini aynı anda tüketti. Sabit backoff grubu bir blok halinde +200 ms, +600 ms ve +1400 ms'ye taşıdı. Her blok kabul sınırını bir kez daha aştı, ama 100'lük bir kuyrukla taşma küçüktü ve sonraki blok yer buldu. Full jitter aynı istekleri yaklaşık yarım saniyeye yaydı. Her işlemin dört deneme hakkı aynı olduğu için toplam istek sayısı bekleme kuralına pek bağlı değildi. Değişen, bu isteklerin zamana nasıl yayıldığıydı; bu da kısa bir tepe değer katı bir sınırı (kabul sınırı, outage) aştığında önem kazandı. *Miktarı* yalnızca retry bütçesi değiştirdi; bunu da kendisinin boyutlandırılmadığı bir grubun çoğunun retry'larını reddederek yaptı.
