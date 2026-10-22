# Distributed Reliability Lab

**Dağıtık sistemlerde arıza senaryolarını, tutarlılık sınırlarını ve toparlanma stratejilerini uygulamalı olarak inceleyen bir laboratuvar.**

**Türkçe** · [English](README.md)

Bu depo, [emregorgulu.com](https://emregorgulu.com) üzerindeki *Dağıtık Sistemlerde Güvenilirlik* yazı dizisinin uygulama alanıdır. Amaç belirli bir teknoloji yığınını savunmak değil; arızaları yeniden üretmek, sonuçlarını gözlemlemek ve mimari önlemleri karşılaştırmaktır.

> **Proje durumu:** E01 (Lost Event) çalıştırılabilir durumda. Deney betiği ve otomatik testleri çalıştırıldı; ayrıntılar için [experiments/transactional-outbox](experiments/transactional-outbox/README.tr.md). E02 ve E03 tanımlandı ancak henüz geliştirilmedi.

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
| **E03 Retry Storm** | Gecikme, geçici hata ve aşırı yük oluşturma | Anlık tekrarlarla sınırlı backoff ve jitter stratejilerini karşılaştırmak | Planlandı |

Arıza noktaları, değişmezler ve kabul ölçütleri için [deney tanımlarına](docs/experiments.tr.md) bakabilirsiniz.

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
│   └── transactional-outbox/   # E01 Lost Event: .NET servisi, deney betiği, xUnit testleri
├── infrastructure/
│   └── compose.yaml            # yerel deneyler için ortak PostgreSQL + RabbitMQ
├── docs/
│   ├── experiments.md
│   └── experiments.tr.md
├── README.md
└── README.tr.md
```

`experiments/idempotency/` ve `experiments/retry-resilience/` planlanıyor. Mevcut klasörler gibi bunları da boş iskeletler olarak değil, çalıştırılabilir kodlarıyla birlikte ekleyeceğiz.

## Yaklaşımımız

1. **Arızayı görünür kılmak:** Hangi noktada müdahale edildiğini ve ne olduğunun nasıl gözlemlendiğini belirtmek.
2. **Önce-sonra karşılaştırması:** Güvensiz başlangıç davranışını gösterip ardından çözümü denemek.
3. **Abartılı garantilerden kaçınmak:** Outbox tam olarak bir kez teslimat demek değildir; her retry güvenli değildir.
4. **Toparlanmayı tasarlamak:** Belirsiz sonuçları, yeniden başlamayı ve yarım kalmış işlem sahipliğini ele almak.
5. **Kanıt sunmak:** Çalıştırılmayan veya test edilmeyen bir deneyi başarılı diye tanıtmamak.

## İlgili yazılar

- Dağıtık Sistemlerde Idempotency — aynı mantıksal isteğin tekrarlı etkileri.
- Transactional Outbox — olay yayımı niyetinin kalıcı hale getirilmesi.
- Retry, Timeout ve Backoff — güvenli yeniden deneme üzerine planlanan yazı.

TR/EN makalelerin doğrulanmış bağlantılarını daha sonra ekleyeceğiz.

## Katkı

Küçük kapsamlı pull request'ler, arıza tekrar üretimleri, test senaryoları ve mimari iddialara yönelik düzeltmeler değerlidir.

## Lisans

[MIT Lisansı](LICENSE) ile yayımlanmıştır. Deneyler eğitim amaçlıdır: güvensiz başlangıç örnekleri bilerek güvensizdir ve yalnızca yerel deney için kullanılmalıdır.
