# Deney tanımları

**E01 geliştirildi ve çalıştırılabilir durumda** (bkz. [experiments/transactional-outbox](../experiments/transactional-outbox/README.tr.md)). **E02 ve E03 planlanan**, henüz geliştirilmemiş deneylerdir. Her deney temiz bir Docker Compose ortamından yeniden üretilebilmeli; açık arıza tetikleyicileri, ilişkilendirme kimlikleri ve doğrulama koşulları içermelidir.

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

## Kanıt formatı

Her deneyde ön koşullar, başlatma/durdurma komutları, arıza tetikleme noktası, yeniden başlatma öncesi/sonrası beklenen durum, assertion'lar, örnek log alanları (`event_id`, `operation_id`, `attempt`, `phase`), sınırlamalar ve kısa arıza analizi bulunmalı.

## Uygulama sırası

Önce E01, ardından aynı altyapıyla E02, son olarak E03. Bilerek güvensiz örnekler açıkça işaretlenmeli ve yalnızca yerel deney için kullanılmalı.
