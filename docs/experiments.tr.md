# Deney tanımları

**E01 ve E03 geliştirildi ve çalıştırılabilir durumda** (bkz. [experiments/transactional-outbox](../experiments/transactional-outbox/README.tr.md) ve [experiments/retry-resilience](../experiments/retry-resilience/README.tr.md)). **E02 planlanan**, henüz geliştirilmemiş bir deneydir. Her deney temiz bir Docker Compose ortamından yeniden üretilebilmeli; açık arıza tetikleyicileri, ilişkilendirme kimlikleri ve doğrulama koşulları içermelidir.

## E01 — Lost Event

**Başlangıç:** İş emrini PostgreSQL'e kaydet; `WorkOrderCreated` olayını ayrı bir adımda RabbitMQ'ya yayımla.

**Arıza:** COMMIT tamamlandıktan hemen sonra, yayım gerçekleşmeden API sürecini sonlandır.

**Beklenen sorun:** İş emri veritabanında bulunur ama broker'da karşılık gelen olay yoktur.

**Önlem:** İş emriyle değiştirilemez outbox olayını aynı veritabanı transaction'ında kaydet. Bekleyen olayları worker yayımlasın.

**Kabul ölçütleri:** (a) Commit edilmiş her iş emri için kalıcı bir outbox kaydı bulunmalı; (b) broker yeniden erişilebilir olunca bekleyen kayıtlar yeniden başlatma sonrası yayımlanabilmeli; (c) rollback edilen iş emri için commit edilmiş outbox olayı bulunmamalı. **Exactly-once** garantisi iddia edilmemeli.

**Uygulama:** [experiments/transactional-outbox](../experiments/transactional-outbox/README.tr.md). Çalıştırmak için `./experiments/transactional-outbox/run-e01.sh`, test etmek için `./experiments/transactional-outbox/test-e01.sh`.

## E02 — Duplicate Delivery

**Arıza:** Broker yayımlamayı onaylasın; ancak outbox kaydı gönderildi olarak işaretlenmeden publisher sonlandırılsın.

**Beklenen gözlem:** Aynı **olay kimliği** yeniden yayımlanabilir.

**Önlem:** Tüketici, işlenen mesaj kimliğini benzersiz veritabanı kısıtıyla, iş verisi güncellemesiyle **aynı transaction içinde** saklasın.

**Kabul ölçütü:** Broker'da birden fazla teslimat olabilir; tüketicinin veritabanındaki iş etkisi aynı olay için bir kez oluşmalı. Transaction dışındaki etkiler için ayrı güvenlik mekanizmaları gerektiği belirtilmeli.

## E03 — Retry Storm

**Başlangıç:** Sınırlı kapasitede çalışan, gecikmesi ve geçici hata oranı artırılabilen bir bağımlı servis oluştur. İstemciler hemen tekrar denesin.

**Arıza:** Başlangıç talep yükünü kontrollü tutarak hata oranını ve gecikmeyi artır.

**Önlem:** Süre sınırları, maksimum deneme sayısı, jitter'lı exponential backoff ve retry bütçesi uygula; aşırı yük reddi ve circuit breaker kararlarını ayrıca değerlendir.

**Kabul ölçütü:** Talep başına deneme sayısı, tamamlanan mantıksal işlemler, bağımlı servise toplam trafik, gecikme dağılımı ve reddedilen/tükenen istekleri raporla. Aynı başlangıç yüküyle karşılaştırma yap; backoff'un tek başına sistem kararlılığını garanti ettiğini iddia etme.

**Uygulama:** [experiments/retry-resilience](../experiments/retry-resilience/README.tr.md). İki senaryoyu birden çalıştırmak için `cd experiments/retry-resilience && docker compose up --build`, beş farklı seed ile tekrarlamak için `./experiments/retry-resilience/repeat-e03.sh`, test etmek için `./experiments/retry-resilience/test-e03.sh`. Tüm stratejiler makaledeki retry döngüsünü (en fazla 4 deneme, deneme başına 2 sn) ve sınırlı kapasiteli, dolunca 503 ile reddeden sınırlı kuyruklu bir bağımlı servisi paylaşır.

- **E03-A — Geçici arıza ve farklı retry stratejileri.** Open-loop Poisson gelişler (saniyede 100 işlem) ve 10 sn boyunca gecikmesi artan, rastgele 503 döndüren bir pencere. A (anında yeniden deneme, güvensiz başlangıç), B (exponential backoff) ve C (backoff + full jitter + işlem başına 5 sn süre sınırı) karşılaştırılır. Burada hatalar senkronize değildir; karşılaştırma, uzun süren bir arızada beklemenin retry'ların *ne zaman* geldiğini nasıl değiştirdiğini gösterir.
- **E03-B — Eşzamanlı arıza ve toparlanma baskısı.** 200 istemcilik closed-loop bir topluluk (saniyede yaklaşık 100 işlem). Bağımlı servis 1,5 sn donar, sonra yeniden başlar: içerideki her istek (raporlanan çalıştırmalarda 132-148) aynı anda hata alır ve yeniden başlayan süreç 5 sn boyunca yarı kapasitede çalışır. Hepsinde aynı 5 sn süre sınırıyla A, B, C ve D (C + istemci tarafında retry bütçesi: yeni işlem başına 0,1 jeton, en fazla 10) karşılaştırılır. Başarı, toplam yük, saniyelik ve 100 ms'lik en yüksek yük, bağımlı servisin içindeki istek sayısı, deneme sayısı, p95/p99 ve toparlanma süresi ayrı metrikler olarak raporlanır.

Deneme sınırları, zaman aşımları, süre sınırları, jitter'lı backoff ve retry bütçesi uygulandı. **Circuit breaker uygulanmadı**; aşırı yük reddi bağımlı servisin sınırlı kuyruğudur ve E03-A'nın derin kuyruklu bir varyantı ayrıca belgelendi.

## Kanıt formatı

Her deneyde ön koşullar, başlatma/durdurma komutları, arıza tetikleme noktası, yeniden başlatma öncesi/sonrası beklenen durum, assertion'lar, örnek log alanları (`event_id`, `operation_id`, `attempt`, `phase`), sınırlamalar ve kısa arıza analizi bulunmalı.

## Uygulama sırası

Önce E01, ardından aynı altyapıyla E02, son olarak E03. (E03 ortak altyapıya ihtiyaç duymadığı için E02'den önce geliştirildi.) Bilerek güvensiz örnekler açıkça işaretlenmeli ve yalnızca yerel deney için kullanılmalı.
