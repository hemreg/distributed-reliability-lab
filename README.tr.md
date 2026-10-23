# Distributed Reliability Lab

**Dağıtık sistemlerde arıza senaryolarını, tutarlılık sınırlarını ve toparlanma stratejilerini uygulamalı olarak inceleyen bir laboratuvar.**

**Türkçe** · [English](README.md)

Bu depo, [emregorgulu.com](https://emregorgulu.com) üzerindeki *Dağıtık Sistemlerde Güvenilirlik* yazı dizisinin uygulama alanıdır. Amaç belirli bir teknoloji yığınını savunmak değil; arızaları yeniden üretmek, sonuçlarını gözlemlemek ve mimari önlemleri karşılaştırmaktır.

> **Proje durumu:** E01 (Lost Event) ve E03 (Retry Storm) çalıştırılabilir durumda. Deneyleri ve otomatik testleri çalıştırıldı; ayrıntılar için [experiments/transactional-outbox](experiments/transactional-outbox/README.tr.md) ve [experiments/retry-resilience](experiments/retry-resilience/README.tr.md). E02 tanımlandı ancak henüz geliştirilmedi.

## Yanıt aradığımız sorular

- Veritabanı transaction'ı tamamlanır ama ilgili olay yayımlanamazsa ne olur?
- Outbox kullanırken bile aynı mesaj neden yeniden gönderilebilir?
- İdempotent tüketici hangi güvenceyi sağlayabilir; bu güvencenin sınırı nerededir?
- Retry ne zaman toparlanmayı kolaylaştırır, ne zaman kesintiyi büyütür?

## Deneyler

| Senaryo | Enjekte edilen arıza | Amaç | Durum |
| --- | --- | --- | --- |
| **E01 Lost Event** | Veritabanı commit'inden sonra, broker yayımından önce çökme | Naif çift yazma ile transactional outbox'ı karşılaştırmak | [Çalıştırılabilir](experiments/transactional-outbox/README.tr.md) |
| **E02 Duplicate Delivery** | Broker onayından sonra, outbox durum güncellemesinden önce çökme | En az bir kez yayımı ve tüketici deduplikasyonunu gözlemlemek | Planlandı |
| **E03 Retry Storm** | E03-A: bir süre boyunca gecikme ve rastgele geçici hatalar; E03-B: çok sayıda istemciyi aynı anda hataya düşüren donma ve yeniden başlatma | Anında yeniden denemeyi sınırlı backoff, jitter ve retry bütçesiyle karşılaştırmak | [Çalıştırılabilir](experiments/retry-resilience/README.tr.md) |

Arıza noktaları, değişmezler ve kabul ölçütleri için [deney tanımlarına](docs/experiments.tr.md) bakabilirsiniz.

### Bir deneyi çalıştırmak

Çalıştırılabilir her deney kendi klasöründen tek komutla başlar; yalnızca Docker (Compose v2) gerekir:

```bash
cd experiments/transactional-outbox    # E01: iki yazma yolunu başlatır; çökme adımlarını README anlatır
docker compose up --build

cd experiments/retry-resilience        # E03: E03-A ve E03-B senaryolarını çalıştırır (~4-5 dk), tabloları yazdırır ve durur
docker compose up --build
```

Enjekte edilen arıza, neyin ölçüldüğü ve sonucun nasıl okunacağı her deneyin README dosyasında anlatılıyor. Temizlemek için `docker compose down` (E01'de verileri de silen `docker compose down -v`) kullanın.

## Teknoloji yığını

- **.NET:** API ve arka plan worker süreçleri
- **PostgreSQL:** İş verisi ve outbox için transaction desteği
- **RabbitMQ:** Asenkron mesajlaşma
- **Docker Compose:** Tekrarlanabilir yerel deney ortamı
- **Otomatik testler ve yapılandırılmış loglar:** Gözlemlenebilir sonuçlar

Bunlar yalnızca uygulama tercihleridir. İncelediğimiz güvenilirlik ilkeleri teknolojiden bağımsızdır.

## Klasör yapısı

```text
distributed-reliability-lab/
├── experiments/
│   ├── transactional-outbox/   # E01 Lost Event: .NET servisi, deney betiği, xUnit testleri
│   └── retry-resilience/       # E03 Retry Storm (E03-A, E03-B): simüle bağımlı servis, yük üretici, xUnit testleri
├── infrastructure/
│   └── compose.yaml            # yerel deneyler için ortak PostgreSQL + RabbitMQ
├── docs/
│   ├── experiments.md
│   └── experiments.tr.md
├── README.md
└── README.tr.md
```

`experiments/idempotency/` (E02) planlanıyor. Mevcut klasörler gibi onu da boş bir iskelet olarak değil, çalıştırılabilir koduyla birlikte ekleyeceğiz.

## Yaklaşımımız

1. **Arızayı görünür kılmak:** Hangi noktada müdahale edildiğini ve ne olduğunun nasıl gözlemlendiğini belirtmek.
2. **Önce-sonra karşılaştırması:** Güvensiz başlangıç davranışını gösterip ardından çözümü denemek.
3. **Abartılı garantilerden kaçınmak:** Outbox tam olarak bir kez teslimat demek değildir; her retry güvenli değildir.
4. **Toparlanmayı tasarlamak:** Belirsiz sonuçları, yeniden başlamayı ve yarım kalmış işlem sahipliğini ele almak.
5. **Kanıt sunmak:** Çalıştırılmayan veya test edilmeyen bir deneyi başarılı diye tanıtmamak.

## İlgili yazılar

1. [Dağıtık Sistemlerde Idempotency](https://emregorgulu.com/tr/makaleler/dagitik-sistemlerde-idempotency/) ([English](https://emregorgulu.com/en/articles/idempotency-in-distributed-systems/)) — aynı mantıksal isteğin tekrarlı etkileri.
2. [Transactional Outbox](https://emregorgulu.com/tr/makaleler/transactional-outbox/) ([English](https://emregorgulu.com/en/articles/transactional-outbox/)) — olay yayımı niyetinin kalıcı hale getirilmesi; kanıtlarını E01 sağlıyor.
3. [Retry, Timeout ve Backoff: Ne Zaman Yeniden Denememeliyiz?](https://emregorgulu.com/tr/makaleler/retry-timeout-ve-backoff/) ([English](https://emregorgulu.com/en/articles/retry-timeout-and-backoff/)) — güvenli yeniden deneme, backoff, jitter ve retry bütçesi; kanıtlarını E03 sağlıyor.

## Katkı

Küçük kapsamlı pull request'ler, arıza tekrar üretimleri, test senaryoları ve mimari iddialara yönelik düzeltmeler değerlidir.

## Lisans

[MIT Lisansı](LICENSE) ile yayımlanmıştır. Deneyler eğitim amaçlıdır: güvensiz başlangıç örnekleri bilerek güvensizdir ve yalnızca yerel deney için kullanılmalıdır.
