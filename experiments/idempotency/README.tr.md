# E02 — Duplicate Delivery (idempotent tüketici)

**Türkçe** · [English](README.md)

İlgili yazılar: [Dağıtık Sistemlerde Idempotency](https://emregorgulu.com/tr/makaleler/dagitik-sistemlerde-idempotency/) ([English](https://emregorgulu.com/en/articles/idempotency-in-distributed-systems/)) ve [Transactional Outbox](https://emregorgulu.com/tr/makaleler/transactional-outbox/) ([English](https://emregorgulu.com/en/articles/transactional-outbox/)).

> **Durum:** Çalıştırılabilir. Aşağıdaki deney betiği ve test paketi, burada listelenen sabit sürümlerle 2026-10-23 tarihinde çalıştırıldı. Başka makinelerde elde edilecek sonuçlar için bir iddiada bulunulmuyor.

## Soru

E01, transactional outbox'ın olay kaybetmediğini gösterdi. Bunun bedeli ters yöndeki arızadır: outbox worker bir mesajı yayımlar, broker onu onaylar ve worker outbox kaydını yayımlandı olarak işaretlemeden ölürse, kayıt yeniden başlatmadan sonra hâlâ beklemededir ve aynı olay bir kez daha yayımlanır. Bu tekrar, tüketicinin verisine ne yapar ve idempotent bir tüketici gerçekte neyi garanti eder?

Senaryo:

1. Outbox worker `WorkOrderCreated` olayını RabbitMQ'ya yayımlar.
2. RabbitMQ mesajı kabul eder (publisher confirm alınır).
3. Worker, outbox kaydını yayımlandı olarak işaretlemeden **önce** sonlandırılır.
4. Yeniden başladığında aynı mesajı, aynı `event_id` ile yeniden yayımlar.
5. Tüketici aynı olayı ikinci kez alır.

Aynı sonuca götüren ikinci, bağımsız bir yol da enjekte ediliyor: tüketici veritabanı transaction'ını commit eder ve ack göndermeden **önce** sonlandırılır; broker mesajı yeniden teslim eder (`redelivered=true`).

Her olayı iki tüketici, her biri kendi kuyruğunda, yan yana alır:

| Tüketici | Her teslimatta ne yapar | Etiket |
| --- | --- | --- |
| `inventory-naive` | Tek transaction: rezervasyon ekle, raftan bir conta kiti düş, `COMMIT`, ardından ack. | **Güvensiz başlangıç senaryosu.** Yalnızca arızayı göstermek için var. |
| `inventory-idempotent` | Aynı transaction, ama önce `INSERT INTO processed_messages (consumer, event_id) ... ON CONFLICT DO NOTHING`. 1 satır: etkiyi uygula ve `COMMIT`. 0 satır: zaten uygulanmış, hiçbir şey yazma. Ardından ack. | Önlem |

İkisi de manuel ack kullanır ve ack'i yalnızca transaction bittikten sonra gönderir. Aralarındaki tek fark tekrar ayıklamadır (deduplication).

### İş etkisi ve tekrarın neden önemli olduğu

Her iş emri ("P-101 pompasının contasını değiştir") bir conta kiti, `SK-101` parçası gerektirir. Tüketici bu parçayı rezerve eder: `part_reservations` tablosuna bir satır ekler ve o parça için `stock_levels.on_hand` değerini bir azaltır. Her tüketicinin kendi stok seviyesi vardır (başlangıçta 10); böylece ikisi doğrudan karşılaştırılabilir.

Naif tüketicide tekrarlanan tek bir olay, tek bir iş emri için iki conta kiti rezerve eder: stok 1 yerine 2 düşer. Gerçek bir depoda sistem raftakinden daha az kit gösterir; sonraki bir iş emrine parça yok denebilir ya da sipariş erken tetiklenebilir. Rezervasyon listesi de teknisyene tek iş için iki kit almasını söyler.

### Yöntem

Deney betiği, bu README ve örnek çıktı aynı sırayı izler:

- **(a) Arızayı yeniden üret:** tekrar yayım ve yeniden teslim; broker'da ve naif tüketicinin tablolarında gözlemlenir.
- **(b) Mekanizmayı uygula:** idempotent tüketicinin, tam olarak aynı teslimatlar sonrasındaki tabloları.
- **(c) Gerçek ölçümlerle doğrula:** worker'ı ve iki tüketiciyi yeniden başlat, yeniden ölç, her sayıyı ve son stoğu doğru değerlerle karşılaştır.

### Kısa cevap (aşağıdaki çalıştırmalardan)

- Outbox worker tek bir `event_id` için iki publisher confirm aldı ve broker bu olayın iki kopyasını tuttu (aynı `message_id`, ikisi de `redelivered=false`). Transactional outbox **en az bir kez yayım** sağlar, tam olarak bir kez değil.
- Naif tüketici bu olay için iki rezervasyon yaptı; dört iş emrinden sonra stoğu 6 yerine 4 oldu.
- İdempotent tüketici aynı altı teslimatı aldı ve her olay için tam olarak bir rezervasyon yaptı; stoğu 6, yani doğru değerdi.
- `COMMIT`'ten sonra, ack'ten önce sonlandırılan tüketici mesajı `redelivered=true` ile yeniden aldı. Naif tüketici yeniden rezerve etti, idempotent tüketici etmedi.
- Worker'ı ve iki tüketiciyi yeniden başlatmak hiçbir sayıyı değiştirmedi.
- Publisher confirm işlenme hakkında hiçbir şey söylemez: iki tüketici durdurulmuşken worker confirm'ünü aldı ve kaydı yayımlandı olarak işaretledi; o anda hiçbir rezervasyon yoktu.

İdempotent tüketici, yalnızca kendi transaction'ı içinde kalan **veritabanı** etkisini fiilen bir kez (effectively-once) gerçekleştirir. Bu transaction'ın dışında kalan her şey (e-posta, başka bir sisteme HTTP çağrısı, bir cihaza komut) bu mekanizmanın kapsamında değildir ve kendi güvenlik önlemini gerektirir.

## Hızlı başlangıç (tek komut)

Docker ve Compose v2 yeterli. Bu klasörde:

```bash
docker compose up --build
```

Bu komut PostgreSQL'i, RabbitMQ'yu ve dört servisi başlatır; hepsi yalnızca loopback'e açılır:

| Adres | Servis | Rol |
| --- | --- | --- |
| http://localhost:18501 | `workorders-api` | `POST /work-orders`: iş emri + outbox kaydı tek transaction'da |
| http://localhost:18502 | `outbox-worker` | bekleyen outbox kayıtlarını publisher confirm ile yayımlar; 1. çökme noktası |
| http://localhost:18503 | `inventory-naive` | tüketici, **güvensiz** başlangıç; 2. çökme noktası |
| http://localhost:18504 | `inventory-idempotent` | `processed_messages` kullanan tüketici; 2. çökme noktası |
| http://localhost:15683 | RabbitMQ yönetim arayüzü (kullanıcı `lab`, şifre `lab`) | `e02.work-order-created.audit` kuyruğu yayımlanan her kopyayı tutar |

İkinci bir terminalde bir kez işlenecek bir iş emri oluşturun:

```bash
curl -X POST localhost:18501/work-orders -H 'X-Operation-Id: ok-1'
```

1. çökme noktası: worker'ı kurun, ardından bir iş emri oluşturun. Worker olayı yayımlar, confirm'ü alır ve kaydı işaretlemeden sonlandırılır. Docker onu yeniden başlatır (`restart: on-failure`) ve worker aynı olayı yeniden yayımlar:

```bash
curl -X POST 'localhost:18502/faults/crash-after-publish-confirm?operation_id=dup-1'
curl -X POST localhost:18501/work-orders -H 'X-Operation-Id: dup-1'
```

2. çökme noktası: iki tüketiciyi de kurun, ardından bir iş emri oluşturun. Her tüketici rezervasyonunu commit eder ve ack'ten önce sonlandırılır. Otomatik yeniden başlatmadan sonra broker mesajı yeniden teslim eder:

```bash
curl -X POST 'localhost:18503/faults/crash-after-commit-before-ack?operation_id=ack-1'
curl -X POST 'localhost:18504/faults/crash-after-commit-before-ack?operation_id=ack-1'
curl -X POST localhost:18501/work-orders -H 'X-Operation-Id: ack-1'
```

Birkaç saniye sonra iş tablolarına bakın:

```bash
docker compose exec postgres psql -U lab -d lab -c "
  SELECT consumer, operation_id, count(*) AS reservations
  FROM part_reservations GROUP BY consumer, operation_id ORDER BY operation_id, consumer;"

docker compose exec postgres psql -U lab -d lab -c "SELECT consumer, initial_on_hand, on_hand FROM stock_levels ORDER BY consumer;"
```

23 Ekim 2026'da gözlemlenen (üç iş emri, yani doğru stok 7):

```text
  consumer  | operation_id | reservations
------------+--------------+--------------
 idempotent | ack-1        |            1
 naive      | ack-1        |            2
 idempotent | dup-1        |            1
 naive      | dup-1        |            2
 idempotent | ok-1         |            1
 naive      | ok-1         |            1
(6 rows)

  consumer  | initial_on_hand | on_hand
------------+-----------------+---------
 idempotent |              10 |       7
 naive      |              10 |       5
(2 rows)
```

Çöken her container bir kez yeniden başlamıştı (`docker inspect -f '{{.RestartCount}}'`) ve audit kuyruğunda 3 iş emri için 4 mesaj vardı. Kurulan arıza yalnızca sürecin belleğinde yaşar ve bir kez tetiklenir; bu yüzden yeniden başlayan süreç normal çalışır.

Her şeyi verilerle birlikte durdurup silmek için:

```bash
docker compose down -v
```

Deney betiği aynı şeyi, her çökme ile yeniden başlatma arasındaki durumu da göstererek adım adım çalıştırır ve her sayıyı kontrol eder.

## Çalıştırma

Depo kök dizininden:

```bash
# Deney betiği: temiz ortam → (a) → (b) → (c) → kontroller → kaldırma (ilk build'den sonra ~40 sn)
./experiments/idempotency/run-e02.sh

# Ortamı sonrasında açık bırakmak için (RabbitMQ arayüzü: http://127.0.0.1:15683, lab/lab)
KEEP=1 ./experiments/idempotency/run-e02.sh
# işiniz bitince
docker compose -f experiments/idempotency/compose.yaml down -v

# Testler (xUnit, Docker içinde, compose PostgreSQL/RabbitMQ'ya karşı), ardından kaldırma
./experiments/idempotency/test-e02.sh

# Betik olmadan eşdeğeri
docker compose -f experiments/idempotency/compose.yaml run --rm --build tests
docker compose -f experiments/idempotency/compose.yaml --profile test down -v
```

`run-e02.sh` 27 beklenen-gözlenen kontrolü yazdırır; herhangi biri tutmazsa sıfırdan farklı bir çıkış koduyla sonlanır. `docker compose up` ile aynı dört servisi kullanır, ancak `E02_RESTART_POLICY=no` ayarlar: çöken container betik durumu kaydedene kadar kapalı kalır, ardından betik onu bir orkestratörün yapacağı gibi kendisi başlatır (`docker compose start`). Varsayılan `docker compose up` betiği çalıştırmaz.

macOS'ta `caffeinate -di ./experiments/idempotency/run-e02.sh` çalıştırma sırasında makinenin uykuya geçmesini engeller (Docker'ın sanal makinesi de onunla birlikte uyur).

## Arıza enjeksiyon noktaları

Tüm arıza enjeksiyonu yalnızca yerel deney içindir. `/faults` uç noktaları `E02_FAULT_INJECTION_ENABLED=true` değilse (`compose.yaml` içinde ayarlı) `400` döner. Arıza hedef sürecin belleğinde kurulur ve verilen `operation_id` için (verilmezse sıradaki mesaj için) en fazla bir kez tetiklenir.

| Tetikleyici | Tam olarak nerede | Etki |
| --- | --- | --- |
| `POST :18502/faults/crash-after-publish-confirm?operation_id=X` | `OutboxPublisher`, `BasicPublishAsync` X için broker confirm'ünü döndürdükten hemen sonra, `UPDATE ... SET published_at` öncesinde | Worker'a **SIGKILL** (çıkış kodu 137). Sahiplenme transaction'ı (`FOR UPDATE SKIP LOCKED`) iptal olur, kayıt yeniden beklemeye düşer. Yeniden başlatmadan sonra aynı `event_id` ikinci kez yayımlanır. |
| `POST :18503/faults/crash-after-commit-before-ack?operation_id=X` (naif) veya `:18504` (idempotent) | `ConsumerHost`, tüketicinin X için transaction'ı commit edildikten sonra, `BasicAckAsync` öncesinde | Tüketiciye **SIGKILL** (çıkış kodu 137). RabbitMQ bağlantı kapanınca ack almamış mesajı kuyruğa geri koyar ve `redelivered=true` ile yeniden teslim eder. |
| `docker compose stop inventory-naive inventory-idempotent` | tüketiciler | A2 adımında kullanılır: hiçbir şey işlenmezken worker yine confirm alır ve kaydı yayımlandı olarak işaretler. |

Sonlandırma gerçek bir süreç çıkışıdır, exception değildir: hiçbir `finally` bloğu çalışmaz, hiçbir transaction commit edilmez, hiçbir ack gönderilmez. Servisler `init: true` ile çalışır; böylece SIGKILL PID 1 için yok sayılmaz. Önce log satırları yazılır; çökme noktası ile sonlandırma arasında olan tek şey budur.

E01'den farklı olarak outbox publisher kendi container'ında (`outbox-worker`) çalışır; onu sonlandırmak API'yi de birlikte götürmez.

## Ne ölçülüyor, nereden

| Kanıt | Kaynak | Neden güvenilir |
| --- | --- | --- |
| Bir olayın broker'daki kopyaları | Exchange'e bağlı, hiç tüketilmeyen `e02.work-order-created.audit` audit kuyruğu. Broker'ın kabul ettiği her mesaj orada kalır; betik yönetim API'si üzerinden (`ackmode: ack_requeue_true`) bakar ve mesajları `message_id` (= outbox `event_id`) başına sayar. | Broker her yayımı bağlı tüm kuyruklara yönlendirir; audit kuyruğu tüketici kuyruklarının gördüğünü görür. |
| Publisher confirm'ler | `outbox-worker`'ın `publish_confirmed` satırları, her confirm için bir tane, iki süreç ömrü boyunca | `BasicPublishAsync` confirm'ü döndürdükten sonra yazılır. |
| Kuyruk derinliği, ack bekleyen mesajlar | `rabbitmqctl list_queues name messages_ready messages_unacknowledged` | Doğrudan kuyruk sürecine sorar; yönetim istatistikleri örneklenir ve gecikebilir. |
| Teslimatlar ve redelivered bayrağı | `consumer_deliveries`: işlemeden önce tüketicinin (autocommit ile) yazdığı, teslimat başına bir satır; `instance` (her süreç başlangıcında rastgele), `delivery_tag` ve `redelivered` ile | İş transaction'ının dışında tutulan laboratuvar enstrümantasyonu; sonrasındaki bir çökme onu silmez. Gerçek bir tüketici bunun yerine log satırı ya da metrik üretir. |
| İş etkileri | `part_reservations` (rezervasyon başına bir satır) ve `stock_levels.on_hand` | Bunlar iş tablolarının kendisidir. |
| Tekrar kararları | `processed_messages` (`PRIMARY KEY (consumer, event_id)`) ve `duplicate_skipped` log satırları | Satır yalnızca etki onunla birlikte commit edildiyse vardır. |

Doğru değerler `work_orders` tablosundan hesaplanır: iş emri başına bir rezervasyon, stok için de `initial_on_hand - sum(quantity)`.

Loglar satır başına tek bir düz JSON nesnesidir: `ts`, `level`, `category`, `phase`, `operation_id` (`X-Operation-Id` başlığından; AMQP `correlation_id`), `event_id` (outbox `event_id`; AMQP `message_id`), `work_order_id`, `consumer`, `delivery_tag`, `redelivered`, `fault`, `count`, `on_hand_after`, `msg`, `error`.

| `phase` | Anlamı |
| --- | --- |
| `fault_armed`, `fault_injected`, `process_killed` | Arıza kuruldu, tetiklendi ve ardından SIGKILL geldi. |
| `outbox_claimed`, `publish_confirmed`, `outbox_marked_published` | Worker bekleyen kayıtları sahiplendi, her kayıt için confirm aldı, kayıtları yayımlandı olarak işaretledi. |
| `delivery_received` | Tüketici bir teslimat aldı (`redelivered` ile). |
| `effect_committed` | Rezervasyon ve stok güncellemesi commit edildi (`on_hand_after` ile). |
| `duplicate_skipped` | İdempotent tüketici: primary key bu `(consumer, event_id)` çiftini zaten içeriyordu; hiçbir şey yazılmadı. |
| `acked` | Transaction'dan sonra `basic.ack` gönderildi. |
| `processing_failed`, `message_rejected` | İşleme hata verdi (nack, 1 sn sonra kuyruğa geri), ya da içerik okunamadı (reddedildi, kuyruğa geri konmadı). |

## Gözlemlenen sonuçlar

Aşağıdakiler, temiz bir durumdan (önce E02 imajları silinerek) 2026-10-23'te yapılan bir çalıştırmanın tam çıktısı olan [`results/sample/run-e02.txt`](results/sample/run-e02.txt) dosyasından alıntılardır. Bazı adımlar atlandı; gösterilen satırlar değiştirilmeden kopyalandı. ID'ler ve zaman damgaları her çalıştırmada farklıdır.

Çalıştırmadan önce iki tüketicinin de 10 conta kiti vardı ve hiç rezervasyon yoktu.

### (a) Arızayı yeniden üretmek

**A3. Worker, broker confirm'ünden sonra, kaydı işaretlemeden önce sonlandırılıyor.** Yeniden başlatmadan önce kayıt hâlâ beklemede, broker'da zaten bir kopya var ve iki tüketici de onu işlemiş:

```text
 operation_id |               event_id               | published_at 
--------------+--------------------------------------+--------------
 op-dup       | 01a166fa-8ebc-7e48-99c7-f68a32f5b602 | 
(1 row)

$ docker compose logs outbox-worker | jq 'select(.phase)' (operation_id=op-dup)
    04:17:38.992  warn     fault_armed              op-dup            -                 fault=crash-after-publish-confirm
    04:17:39.045  info     publish_confirmed        op-dup            01a166fa-8ebc...  
    04:17:39.046  warn     fault_injected           op-dup            01a166fa-8ebc...  fault=crash-after-publish-confirm
    04:17:39.046  critical process_killed           op-dup            01a166fa-8ebc...  fault=crash-after-publish-confirm
```

`docker compose start outbox-worker` sonrasında aynı olay yeniden yayımlanıyor ve kayıt işaretleniyor:

```text
    04:17:42.015  info     broker_connected         op-dup            01a166fa-8ebc...  
    04:17:42.019  info     publish_confirmed        op-dup            01a166fa-8ebc...  
    04:17:42.040  info     outbox_marked_published  -                 -                 count=1
$ management API: POST /api/queues/%2F/e02.work-order-created.audit/get (ackmode ack_requeue_true: peek, messages stay)
    audit queue e02.work-order-created.audit: 4 message(s) in total
      message_id=01a166fa-8ebc-7e48-99c7-f68a32f5b602  operation_id=op-dup  work_order_id=01a166fa-8ebc-7513-b0df-814bc52398e2
      message_id=01a166fa-8ebc-7e48-99c7-f68a32f5b602  operation_id=op-dup  work_order_id=01a166fa-8ebc-7513-b0df-814bc52398e2
```

Naif tüketici iki kopyayı da aldı, hiçbiri yeniden teslim olarak işaretli değildi, ve iki kez rezerve etti:

```text
 operation_id |               event_id               | instance | delivery_tag | redelivered |          received_at          
--------------+--------------------------------------+----------+--------------+-------------+-------------------------------
 op-dup       | 01a166fa-8ebc-7e48-99c7-f68a32f5b602 | 2543b0c7 |            2 | f           | 2026-10-23 04:17:39.046261+00
 op-dup       | 01a166fa-8ebc-7e48-99c7-f68a32f5b602 | 2543b0c7 |            3 | f           | 2026-10-23 04:17:42.018685+00
(2 rows)

 operation_id |               event_id               |            reservation_id            | part_no | quantity |          reserved_at          
--------------+--------------------------------------+--------------------------------------+---------+----------+-------------------------------
 op-dup       | 01a166fa-8ebc-7e48-99c7-f68a32f5b602 | 01a166fa-8ee7-74b7-b465-81fb4577a24d | SK-101  |        1 | 2026-10-23 04:17:39.047567+00
 op-dup       | 01a166fa-8ebc-7e48-99c7-f68a32f5b602 | 01a166fa-9a83-738d-82a5-cac237b60053 | SK-101  |        1 | 2026-10-23 04:17:42.01944+00
(2 rows)
```

Broker için bunlar iki ayrı mesajdır. Onları birbirine bağlayan yalnızca `event_id`'dir; `redelivered` bayrağı publisher tarafındaki bir tekrarı ortaya çıkaramaz.

**A4. İki tüketici de `COMMIT`'ten sonra, ack'ten önce sonlandırılıyor.** Naif tüketicinin logu sonlandırmayla bitiyor; `acked` satırı yok ve ack almamış mesaj iki kuyruğa da geri dönmüş:

```text
    04:17:44.551  info     delivery_received        op-ack            01a166fa-a40f...  redelivered=false
    04:17:44.552  info     effect_committed         op-ack            01a166fa-a40f...  redelivered=false on_hand_after=5
    04:17:44.552  warn     fault_injected           op-ack            01a166fa-a40f...  fault=crash-after-commit-before-ack redelivered=false
    04:17:44.553  critical process_killed           op-ack            01a166fa-a40f...  fault=crash-after-commit-before-ack redelivered=false
effect_committed, then the kill: no acked line. The broker requeued the unacked message:
$ rabbitmqctl list_queues name messages_ready messages_unacknowledged
    name                             messages_ready  messages_unacknowledged
    e02.inventory.idempotent         1               0
    e02.work-order-created.audit     5               0
    e02.inventory.naive              1               0
```

Yeniden başlatmadan sonra yeni bir süreç örneği mesajı `redelivered=true` ile alıyor ve naif tüketici yeniden rezerve ediyor:

```text
 operation_id |               event_id               | instance | delivery_tag | redelivered |          received_at          
--------------+--------------------------------------+----------+--------------+-------------+-------------------------------
 op-ack       | 01a166fa-a40f-7693-900c-53292ec798be | 2543b0c7 |            4 | f           | 2026-10-23 04:17:44.550851+00
 op-ack       | 01a166fa-a40f-7693-900c-53292ec798be | 30dc7458 |            1 | t           | 2026-10-23 04:17:47.319393+00
(2 rows)
```

**A5. Dört iş emrinden sonra naif tüketicinin iş tabloları:**

```text
  operation_id   |               event_id               | reservations | units_reserved 
-----------------+--------------------------------------+--------------+----------------
 op-ok           | 01a166fa-7383-702a-83cc-eedbbaf8ac2a |            1 |              1
 op-confirm-only | 01a166fa-7d5c-742b-a869-9f43d762b959 |            1 |              1
 op-dup          | 01a166fa-8ebc-7e48-99c7-f68a32f5b602 |            2 |              2
 op-ack          | 01a166fa-a40f-7693-900c-53292ec798be |            2 |              2
(4 rows)

  consumer  | part_no | initial_on_hand | on_hand | units_taken 
------------+---------+-----------------+---------+-------------
 idempotent | SK-101  |              10 |       6 |           4
 naive      | SK-101  |              10 |       4 |           6
(2 rows)
```

**A2. Confirm, işlenme değildir.** İki tüketici durdurulmuşken worker confirm'ünü aldı ve kaydı yayımlandı olarak işaretledi (`published_at 2026-10-23 04:17:35.012801+00`). O anda her tüketici kuyruğunda bir mesaj bekliyordu ve `part_reservations` tablosunda `op-confirm-only` için hiç satır yoktu.

### (b) Mekanizmayı uygulamak

İdempotent tüketici aynı altı teslimatı aldı; tekrar eden kopya (`op-dup`, iki kez `f`) ve yeniden teslim (`op-ack`, `t`) dahil:

```text
  operation_id   |               event_id               | instance | delivery_tag | redelivered |          received_at          
-----------------+--------------------------------------+----------+--------------+-------------+-------------------------------
 op-ok           | 01a166fa-7383-702a-83cc-eedbbaf8ac2a | e8120831 |            1 | f           | 2026-10-23 04:17:32.486975+00
 op-confirm-only | 01a166fa-7d5c-742b-a869-9f43d762b959 | 627228f7 |            1 | f           | 2026-10-23 04:17:37.806515+00
 op-dup          | 01a166fa-8ebc-7e48-99c7-f68a32f5b602 | 627228f7 |            2 | f           | 2026-10-23 04:17:39.046055+00
 op-dup          | 01a166fa-8ebc-7e48-99c7-f68a32f5b602 | 627228f7 |            3 | f           | 2026-10-23 04:17:42.018735+00
 op-ack          | 01a166fa-a40f-7693-900c-53292ec798be | 627228f7 |            4 | f           | 2026-10-23 04:17:44.550853+00
 op-ack          | 01a166fa-a40f-7693-900c-53292ec798be | 40b30208 |            1 | t           | 2026-10-23 04:17:47.317544+00
(6 rows)
```

`processed_messages` her olay için bir satır tutuyor ve rezervasyonlar onu izliyor:

```text
  consumer  |               event_id               |         processed_at          
------------+--------------------------------------+-------------------------------
 idempotent | 01a166fa-7383-702a-83cc-eedbbaf8ac2a | 2026-10-23 04:17:32.489775+00
 idempotent | 01a166fa-7d5c-742b-a869-9f43d762b959 | 2026-10-23 04:17:37.808254+00
 idempotent | 01a166fa-8ebc-7e48-99c7-f68a32f5b602 | 2026-10-23 04:17:39.046995+00
 idempotent | 01a166fa-a40f-7693-900c-53292ec798be | 2026-10-23 04:17:44.551753+00
(4 rows)

  operation_id   |               event_id               | reservations | units_reserved 
-----------------+--------------------------------------+--------------+----------------
 op-ok           | 01a166fa-7383-702a-83cc-eedbbaf8ac2a |            1 |              1
 op-confirm-only | 01a166fa-7d5c-742b-a869-9f43d762b959 |            1 |              1
 op-dup          | 01a166fa-8ebc-7e48-99c7-f68a32f5b602 |            1 |              1
 op-ack          | 01a166fa-a40f-7693-900c-53292ec798be |            1 |              1
(4 rows)

    04:17:42.020  duplicate_skipped  op-dup  redelivered=false  (processed_messages PK conflict; nothing written)
    04:17:47.320  duplicate_skipped  op-ack  redelivered=true  (processed_messages PK conflict; nothing written)
```

### (c) Doğrulamak

`docker compose restart outbox-worker inventory-naive inventory-idempotent` ve 3 sn beklemeden önce ve sonra bütün sayılar:

```text
--- C1. Measurements before the restart
    reservations naive=6 idempotent=4 | on_hand naive=4 idempotent=6 | processed_messages=4 | deliveries=12 | pending_outbox=0 | audit_copies=5 | queued naive=0 idempotent=0
--- C3. Measurements after the restart
    reservations naive=6 idempotent=4 | on_hand naive=4 idempotent=6 | processed_messages=4 | deliveries=12 | pending_outbox=0 | audit_copies=5 | queued naive=0 idempotent=0
```

Olay başına yan yana:

```text
  operation_id   | broker_copies | naive_deliveries  | naive_reservations | idempotent_deliveries | idempotent_reservations 
-----------------+---------------+-------------------+--------------------+-----------------------+-------------------------
 op-ok           |             1 | 1 (0 redelivered) |                  1 | 1 (0 redelivered)     |                       1
 op-confirm-only |             1 | 1 (0 redelivered) |                  1 | 1 (0 redelivered)     |                       1
 op-dup          |             2 | 2 (0 redelivered) |                  2 | 2 (0 redelivered)     |                       1
 op-ack          |             1 | 2 (1 redelivered) |                  2 | 2 (1 redelivered)     |                       1
(4 rows)
```

Tüketici başına son iş durumu, doğru değerlerin yanında:

```text
  consumer  | work_orders | broker_copies |    deliveries     | reservations | correct_reservations | on_hand | correct_on_hand | difference 
------------+-------------+---------------+-------------------+--------------+----------------------+---------+-----------------+------------
 idempotent |           4 |             5 | 6 (1 redelivered) |            4 |                    4 |       6 |               6 |          0
 naive      |           4 |             5 | 6 (1 redelivered) |            6 |                    4 |       4 |               6 |         -2
(2 rows)
```

### Betiğin yazdırdığı kontroller

```text
1. The same event_id is published twice (crash after broker confirm, before marking)
  OK   outbox-worker killed after the broker confirm (exit code)              expected=137 observed=137
  OK   op-dup outbox row still pending after the crash                        expected=1 observed=1
  OK   op-dup copies at the broker before the worker restart                  expected=1 observed=1
  OK   op-dup copies at the broker after the restart (same event_id)          expected=2 observed=2
  OK   op-dup publisher confirms logged by the worker                         expected=2 observed=2
  OK   outbox rows pending after the restart                                  expected=0 observed=0
  OK   op-dup deliveries to naive / idempotent, both redelivered=false        expected=2/2 observed=2/2
   A publisher confirm is not processing
  OK   op-confirm-only confirmed and marked published, consumers stopped      expected=1 observed=1
  OK   op-confirm-only messages waiting in the consumer queues                expected=2 observed=2
  OK   op-confirm-only reservations at that moment                            expected=0 observed=0
2. Naive consumer (UNSAFE baseline): two business effects for one event_id
  OK   op-ok control: copies at the broker / naive reservations               expected=1/1 observed=1/1
  OK   op-dup naive reservations                                              expected=2 observed=2
3. Idempotent consumer: one business effect for that event_id
  OK   op-dup idempotent reservations                                         expected=1 observed=1
4. Consumer killed after COMMIT, before ack: the broker redelivers
  OK   inventory-naive killed after commit (exit code)                        expected=137 observed=137
  OK   inventory-idempotent killed after commit (exit code)                   expected=137 observed=137
  OK   op-ack copies at the broker (published once)                           expected=1 observed=1
  OK   op-ack unacked message back in the queue: naive / idempotent           expected=1/1 observed=1/1
  OK   op-ack deliveries with redelivered=true: naive / idempotent            expected=1/1 observed=1/1
  OK   op-ack naive reservations (another effect)                             expected=2 observed=2
  OK   op-ack idempotent reservations                                         expected=1 observed=1
  OK   idempotent duplicate_skipped log lines (op-dup + op-ack)               expected=2 observed=2
5. Restart of worker and consumers: no new effects, final state consistent
  OK   C1 vs C3: every count unchanged by the restart                         expected=same observed=same
  OK   idempotent: events without exactly one reservation                     expected=0 observed=0
  OK   idempotent: published events without a reservation                     expected=0 observed=0
  OK   naive: events reserved more than once                                  expected=2 observed=2
  OK   idempotent: SK-101 on hand equals the correct stock (6)                expected=6 observed=6
  OK   naive: SK-101 on hand, 2 below the correct stock (6)                   expected=4 observed=4

27 of 27 checks OK.
```

Deney betiği 2026-10-23'te üç çalıştırmada 27 kontrolün tamamını geçti: ikisi mevcut imajlarla, biri E02 imajları silindikten sonra (yerel build önbelleğinden imaj build'i dahil 38,5 sn).

## Bu deney neyi gösteriyor

1. **Outbox en az bir kez yayımlar.** Broker confirm'ü ile outbox güncellemesi arasındaki bir çökme aynı `event_id`'yi yeniden yayımlar. Outbox bunu engelleyemez: kaydın işaretlenmesi ile broker'ın mesajı kabul etmesi iki ayrı sistemdedir ve worker bunları birlikte commit edemez.
2. **Publisher confirm, tüketiciler hakkında değil broker hakkında bir bilgidir.** Broker'ın mesajın sorumluluğunu üstlendiği (bağlı kalıcı kuyruklara yönlendirdiği) anlamına gelir. A2'de confirm geldi ve kayıt yayımlandı olarak işaretlendi; o sırada hiçbir tüketici bir şey yapmamıştı.
3. **Tekrarlar tüketiciye iki farklı yoldan ulaşır** ve broker bunlardan yalnızca birini görebilir: publisher tarafındaki tekrar yeni bir mesajdır (`redelivered=false`); kaybolan bir ack sonrasındaki yeniden teslim aynı mesajdır (`redelivered=true`). Tekrar ayıklama, redelivered bayrağına değil, olayın iş kimliğine (`event_id`) dayanmalıdır.
4. **Ack'i commit'ten sonra göndermek gereklidir ama yeterli değildir.** İki tüketici de ack'i yalnızca `COMMIT`'ten sonra gönderir; bu yüzden hiçbiri etki kaybetmedi. Naif tüketici yine de yeniden teslim edilen mesajı ikinci kez uyguladı.
5. **Aynı transaction içindeki bir benzersizlik kısıtı, tüketicinin veritabanı etkisini fiilen bir kez gerçekleştirir.** İşaret ve etki birlikte commit edilir ya da hiçbiri edilmez; eşzamanlı iki teslimattan hangisinin kazanacağına önceden yapılan bir okuma değil, primary key karar verir (bkz. testler).
6. **Bir tekrarın zararı broker'da değil, iş verisinde görünür:** tek bir iş emri için iki rezervasyon ve dört iş emrinden sonra gerçeğin 2 altında bir stok seviyesi.

## Bu deney neyi göstermiyor

- **Genel olarak exactly-once teslimat ya da işleme.** Burada broker'ın bir mesajı birden fazla kez teslim etmesini engelleyen hiçbir şey yok. Tüketici belirli bir etkiyi idempotent yapıyor: kendi PostgreSQL transaction'ı içindeki yazmaları.
- **Dış yan etkiler.** Tüketici ayrıca bir e-posta gönderseydi, başka bir sisteme HTTP ile çağrı yapsaydı ya da bir cihaza komut gönderseydi, bu çağrı transaction'ın dışında kalırdı. `COMMIT`'ten önce yapılırsa, transaction rollback olduğunda ya da süreç `COMMIT`'ten önce ölüp mesaj yeniden teslim edildiğinde tekrarlanır. `COMMIT`'ten sonra yapılırsa, süreç `COMMIT` ile çağrı arasında ölürse kaybolur; çünkü yeniden teslim bu kez tekrar olarak tanınır ve atlanır. Bu tür etkiler, alıcı sistemin dikkate aldığı bir idempotency anahtarı ya da tüketici tarafında bir outbox gibi kendi önlemlerini gerektirir. Bunların hiçbiri uygulanmadı.
- **Üretim koşullarında eşzamanlı tüketiciler.** Aynı olayın eşzamanlı iki teslimatı süreç içinde (iki bağlantı, biri açık tutularak) test ediliyor; yük altında aynı kuyrukta yarışan iki tüketici süreciyle değil.
- **Zehirli mesajlar (poison messages).** Her zaman hata veren bir mesaj nack edilir ve (1 sn araya koyarak) sonsuza kadar yeniden teslim edilir. Deneme sınırı ve dead-letter kuyruğu yok. Kapsam dışı.
- **`processed_messages` için saklama süresi.** Tablo, tüketici başına her olay için bir satır büyür ve hiç temizlenmez. Eski satırları silmek bir ödünleşimdir: çok erken silinen bir satır, geç gelen bir tekrarın (örneğin yedekten geri yüklenmiş bir outbox'tan yeniden yayım) geçmesine izin verir. Saklama penceresi, bir tekrarın hâlâ gelebileceği en uzun süreden uzun olmalıdır. Uygulanmadı.
- **Sıralama.** İncelenmedi.
- **Broker ya da veritabanı arızaları.** Yalnızca uygulama süreçleri sonlandırılıyor. Simüle edilmeyenler: PostgreSQL çökmesi, sonucu bilinmeyen bir `COMMIT`, ağ bölünmeleri, broker yeniden başlatmaları, quorum kuyrukları, çok düğümlü RabbitMQ.

## Tüketici tasarımı

İdempotent yol, tek transaction içinde (`ReservationHandler.HandleAsync`):

```sql
BEGIN;
INSERT INTO processed_messages (consumer, event_id) VALUES ($1, $2)
  ON CONFLICT (consumer, event_id) DO NOTHING;
-- row count 0: already applied by this consumer -> ROLLBACK, nothing written, ack
-- row count 1: first time:
INSERT INTO part_reservations (reservation_id, consumer, event_id, work_order_id, ...) VALUES (...);
UPDATE stock_levels SET on_hand = on_hand - $quantity WHERE consumer = $1 AND part_no = $part RETURNING on_hand;
COMMIT;
-- then basic.ack
```

Neden bu biçim:

- **Karar veren bir okuma değil, kısıttır.** `SELECT ... WHERE event_id = ...` ardından `INSERT` yapılırsa, eşzamanlı iki teslimat da "işlenmemiş" okuyabilir ve ikisi de etkiyi uygular. Primary key ile ikinci `INSERT`, birinci transaction'ın henüz commit edilmemiş anahtarını bekler; birinci commit edince 0 satır alır, birinci rollback olursa 1 satır alır ve etkiyi kendisi uygular. Testlerden biri birinci transaction'ı açık tutarak ikincinin beklediğini gösteriyor.
- **Aynı transaction.** İşaret ayrı yazılsaydı, işaret ile iş commit'i arasındaki bir çökme olayı işaretlenmiş ama uygulanmamış bırakırdı; yeniden teslim atlanır ve bir rezervasyon kaybolurdu.
- **Tüketici başına kapsam.** Anahtar `(consumer, event_id)`: aynı olayın başka bir tüketicisi tekrarları bağımsız olarak ayıklar.
- **Önce işaret.** İşareti iş yazmalarından önce eklemek, bir tekrarı ucuz hale getirir (iş yazması yok, geri alınacak bir şey yok) ve eşzamanlı bir tekrarın erken beklemesini sağlar.
- **Transaction'dan sonra manuel ack, prefetch 1.** Ack'ten önceki bir çökme kayıp bir mesaja değil, yeniden teslime yol açar. Düzgün kapanışta tüketici iptal edilir ve işlenmekte olan teslimatın bitmesi beklenir.
- **İş tablosunda doğal anahtar kısıtı yok.** `part_reservations` tablosunda benzersiz `(consumer, work_order_id)` bilerek yok; böylece naif başlangıç tekrarı gösterebiliyor. Gerçek bir sistemde, iş kuralı izin veriyorsa (burada: iş emri başına bir conta kiti rezervasyonu) böyle bir kısıt yararlı ikinci bir önlemdir; ama her etkiye özgüdür. `processed_messages` ise geneldir.

İki tüketici de kolaylık için üretici ile aynı PostgreSQL veritabanını (`lab`) kullanıyor. Bu, garantiyi etkilemez: tüketicinin transaction'ı yalnızca kendi tablolarını kapsar ve üreticinin transaction'ıyla ilgisi yoktur.

## Otomatik testler

`tests/Idempotency.Tests` (xUnit v3, 12 test) compose'daki gerçek PostgreSQL ve RabbitMQ'ya karşı, ayrı bir veritabanında (`e02_tests`) ve ayrı exchange ve kuyruk adlarıyla çalışır; böylece betiğin durumuna hiç dokunmaz. Uçtan uca testler gerçek outbox worker'ı ve tüketicileri **ayrı işletim sistemi süreçleri** olarak başlatır; enjekte edilen çökmeler gerçek SIGKILL'lerdir ve her yeniden başlatma yeni bir süreçtir.

| Test | Ölçüt |
| --- | --- |
| `C1_crash_between_broker_confirm_and_marking_publishes_the_same_event_id_twice` | 1. Worker bir `publish_confirmed` sonrasında, `outbox_marked_published` olmadan 137 ile çıkar; kayıt beklemededir ve audit kuyruğunda 1 kopya vardır. Yeni bir worker'dan sonra audit kuyruğunda 2 kopya vardır; ikisinde de `message_id` = `event_id` ve `redelivered=false`. |
| `C2_C3_duplicate_publication_naive_consumer_reserves_twice_idempotent_consumer_once` | 2, 3. İki tüketici çalışırken aynı çökme: her biri 2 teslimat alır (`false, false`); naif 2 rezervasyon ve stok −2, idempotent 1 rezervasyon, stok −1, 1 `processed_messages` satırı, 1 `duplicate_skipped`. |
| `C4_consumer_crash_after_commit_before_ack_is_redelivered_naive_reserves_again_idempotent_does_not` | 4. İki tüketici de `acked` satırı olmadan 137 ile çıkar; her birinde commit edilmiş 1 rezervasyon; mesaj her kuyruğa geri dönmüştür; olay bir kez yayımlanmıştır. Yeni süreçlerden sonra: teslimatlar `false, true`; naif 2 rezervasyon, idempotent 1. |
| `C5_restarting_worker_and_consumers_creates_no_new_effects` | 5. Üç olay ve broker düzeyinde bir tekrar; anlık görüntü (rezervasyonlar, stok, `processed_messages`, teslimatlar); üç sürecin düzgün kapatılması, yeni süreçler, 3 sn; anlık görüntü değişmemiş, bekleyen yok, kuyruklar boş. |
| `Publisher_confirm_does_not_mean_a_consumer_processed_the_event` | Hiçbir tüketici çalışmazken bir confirm ve `published_at` vardır: her tüketici kuyruğunda 1 mesaj, 0 rezervasyon, stok değişmemiş. |
| `Naive_baseline_reserves_again_on_every_delivery_of_the_same_event` | Bir olayın üç teslimatı: 3 rezervasyon, stok −3. |
| `Idempotent_consumer_applies_an_event_once_and_skips_later_deliveries` | Üç teslimat: `Applied, Duplicate, Duplicate`; 1 rezervasyon. |
| `Two_concurrent_deliveries_of_the_same_event_id_the_primary_key_lets_exactly_one_win` | İki handler, iki bağlantı. Birincisi yazdıktan sonra transaction'ını açık tutar; ikincisi 500 ms sonra hâlâ bekler. Bırakıldıktan sonra: bir `Applied`, bir `Duplicate`, 1 rezervasyon. |
| `Sixteen_concurrent_deliveries_of_the_same_event_id_create_exactly_one_reservation` | Aynı anda bırakılan 16 handler: tam olarak 1 `Applied`, 15 `Duplicate`, 1 rezervasyon. |
| `A_rolled_back_transaction_leaves_no_processed_marker_so_the_redelivery_applies_the_effect` | `COMMIT` öncesi hata: işaret yok, rezervasyon yok, stok değişmemiş; sonraki teslimat `Applied`, ondan sonraki `Duplicate`. |
| `Duplicate_detection_is_scoped_per_consumer` | Aynı olay `idempotent` için bir kez ve `idempotent-b` için bir kez uygulanır; sonrasında her biri için tekrardır. |
| `Processed_messages_has_a_primary_key_on_consumer_and_event_id` | Şemada `PRIMARY KEY (consumer, event_id)` vardır. |

`./experiments/idempotency/test-e02.sh` çıktısı (2026-10-23; servislerin kendi log satırları çıkarıldı):

```text
  Passed Idempotency.Tests.DuplicateDeliveryTests.C2_C3_duplicate_publication_naive_consumer_reserves_twice_idempotent_consumer_once [1 s]
  Passed Idempotency.Tests.DuplicateDeliveryTests.C5_restarting_worker_and_consumers_creates_no_new_effects [4 s]
  Passed Idempotency.Tests.DuplicateDeliveryTests.C4_consumer_crash_after_commit_before_ack_is_redelivered_naive_reserves_again_idempotent_does_not [1 s]
  Passed Idempotency.Tests.DuplicateDeliveryTests.C1_crash_between_broker_confirm_and_marking_publishes_the_same_event_id_twice [649 ms]
  Passed Idempotency.Tests.DuplicateDeliveryTests.Publisher_confirm_does_not_mean_a_consumer_processed_the_event [264 ms]
  Passed Idempotency.Tests.ConsumerTransactionTests.Sixteen_concurrent_deliveries_of_the_same_event_id_create_exactly_one_reservation [25 ms]
  Passed Idempotency.Tests.ConsumerTransactionTests.Duplicate_detection_is_scoped_per_consumer [17 ms]
  Passed Idempotency.Tests.ConsumerTransactionTests.Processed_messages_has_a_primary_key_on_consumer_and_event_id [13 ms]
  Passed Idempotency.Tests.ConsumerTransactionTests.Two_concurrent_deliveries_of_the_same_event_id_the_primary_key_lets_exactly_one_win [534 ms]
  Passed Idempotency.Tests.ConsumerTransactionTests.Naive_baseline_reserves_again_on_every_delivery_of_the_same_event [29 ms]
  Passed Idempotency.Tests.ConsumerTransactionTests.A_rolled_back_transaction_leaves_no_processed_marker_so_the_redelivery_applies_the_effect [20 ms]
  Passed Idempotency.Tests.ConsumerTransactionTests.Idempotent_consumer_applies_an_event_once_and_skips_later_deliveries [16 ms]

Test Run Successful.
Total tests: 12
     Passed: 12
```

Bir sağlama olarak test paketini idempotent tüketicinin bilerek bozulmuş üç varyantına karşı çalıştırdım, ardından kodu geri yükledim ve paket yeniden geçti (12/12):

| Bozuk varyant | Başarısız testler | Hangileri |
| --- | --- | --- |
| A. `processed_messages` INSERT'ü iş transaction'ının dışında, kendi autocommit komutunda | 12 testten 2'si | rollback testi (işaret, rollback olan rezervasyondan sonra kalıyor ve yeniden teslim atlanıyor) ve eşzamanlı iki teslimat testi (ikinci teslimat artık beklemiyor) |
| B. `processed_messages` üzerinde primary key yok, önce kontrol sonra ekleme (`SELECT EXISTS`, ardından `INSERT`) | 12 testten 3'ü | iki eşzamanlılık testi (1 yerine 2 ve 16 rezervasyon) ve şema testi. Uçtan uca tüm çökme testleri dahil, sıralı her test yine geçti. |
| C. Tekrar ayıklama kaldırıldı (idempotent tüketici naif gibi davranıyor) | 12 testten 8'i | naif, üretici tarafı ve şema testleri dışındaki hepsi |

B varyantı öğretici olanı: önce kontrol edip sonra ekleyen bir tüketici, tekrarları art arda teslim eden her testi geçiyor. Onu yalnızca eşzamanlı tekrarlar ortaya çıkarıyor.

**Testler neyi garanti eder, neyi etmez?** Testler compose'daki gerçek PostgreSQL 18.6 ve RabbitMQ 4.2.9'a karşı, gerçek süreç sonlandırmalarıyla ve aşağıda sabitlenen .NET sürümüyle çalışır. Şunları doğrularlar: broker confirm'ü ile işaretleme arasındaki bir çökmenin aynı `event_id`'yi yeniden yayımladığını; naif tüketicinin her teslimatı uyguladığını, idempotent tüketicinin ise `(consumer, event_id)` başına bir kez uyguladığını (kaybolan bir ack sonrasındaki yeniden teslimde, süreç içi eşzamanlı teslimatlarda ve rollback olan bir denemeden sonra da); ve yeniden başlatmaların yeni etki oluşturmadığını. Yük altında aynı kuyrukta yarışan iki tüketici sürecini, broker ya da veritabanı arızalarındaki davranışı, dış yan etkileri, zehirli mesajları, `processed_messages` saklama süresini ya da performansı **doğrulamazlar**. Eşzamanlılık tek süreç içinde en fazla 16 eşzamanlı handler ile test edilir. Testlerin geçmesi üretim kullanımı hakkında bir şey söylemez: bu kod bir laboratuvardır, kütüphane değildir.

## Klasör yapısı

```text
experiments/idempotency/
├── compose.yaml              # "drlab-e02" projesi: `docker compose up --build` → API, outbox worker, iki tüketici; test profili
├── infrastructure.env        # dahil edilen altyapı için RabbitMQ arayüzünü 15683'e taşır (E01: 15682)
├── Dockerfile                # build / test / runtime aşamaları; iki programı içeren tek runtime imajı
├── run-e02.sh                # deney betiği (a) → (b) → (c), 27 kontrol, kaldırma
├── test-e02.sh               # xUnit paketini Docker içinde çalıştırır, ortamı kaldırır
├── Idempotency.slnx
├── results/sample/run-e02.txt  # referans çalıştırmanın tam çıktısı
├── src/Lab.Shared/           # JSON konsol logları, SIGKILL ile sonlandırma, şema kurulumu, tek seferlik ArmedFault
├── src/WorkOrders/           # üretici (E01'in outbox yolu), `api` ve `outbox-worker` rolleri
│   ├── Program.cs            # POST /work-orders (api), POST /faults/crash-after-publish-confirm (worker), GET /health
│   ├── WorkOrderService.cs   # iş emri + outbox kaydı tek transaction'da
│   ├── OutboxPublisher.cs    # sahiplenme (FOR UPDATE SKIP LOCKED) → confirm ile yayım → 1. çökme noktası → işaretleme
│   ├── Broker.cs             # publisher confirm, mandatory=true, exchange + audit kuyruğu
│   └── schema.sql            # work_orders, outbox_messages, değiştirilemezlik trigger'ı
├── src/Inventory/            # tüketici, `naive` ve `idempotent` modları
│   ├── Program.cs            # GET /health (yalnızca tüketirken 200), POST /faults/crash-after-commit-before-ack
│   ├── ConsumerHost.cs       # manuel ack, prefetch 1, teslimat kaydı, 2. çökme noktası, yeniden bağlanma döngüsü
│   ├── ReservationHandler.cs # iş transaction'ı; idempotent modda processed_messages
│   └── schema.sql            # stock_levels, part_reservations, processed_messages, consumer_deliveries
└── tests/Idempotency.Tests/  # gerçek PostgreSQL + RabbitMQ'ya karşı xUnit v3 testleri
```

## Kullanılan sürümler

| Bileşen | Sürüm |
| --- | --- |
| .NET SDK / ASP.NET Core runtime imajları | `mcr.microsoft.com/dotnet/sdk:10.0.402`, `mcr.microsoft.com/dotnet/aspnet:10.0.13` (raporlanan runtime: .NET 10.0.13) |
| PostgreSQL | `postgres:18.6-alpine` |
| RabbitMQ | `rabbitmq:4.2.9-management-alpine` |
| NuGet | Npgsql 10.0.3, RabbitMQ.Client 7.2.2, xunit.v3 3.2.2, xunit.runner.visualstudio 3.1.5, Microsoft.NET.Test.Sdk 18.11.0 |

## Ön koşullar

- Compose v2.20 veya üstü içeren Docker (`include` desteği için). macOS (arm64) üzerinde Docker 28.4 / Compose 2.39 ile denendi.
- Host üzerinde `curl` ve `jq` (yalnızca `run-e02.sh` kullanıyor).
- Yerel .NET SDK gerekmez.
- Boş loopback portları: `127.0.0.1:18501`-`18504` (servisler) ve `127.0.0.1:15683` (RabbitMQ yönetim arayüzü/API'si). `DRLAB_E02_API_PORT`, `DRLAB_E02_WORKER_PORT`, `DRLAB_E02_NAIVE_PORT`, `DRLAB_E02_IDEMPOTENT_PORT` ve `DRLAB_RABBITMQ_UI_PORT` ile değiştirilebilir. PostgreSQL host'a port açmaz; `docker compose exec postgres psql -U lab -d lab` kullanın.

`drlab-e02` compose proje adı; konteyner, ağ ve volume'ları E01, E03 ve diğer yerel ortamlardan ayrı tutar. E01 RabbitMQ arayüzünü 15682'de, E02 ise 15683'te yayımlar (yalnızca E02'nin ortak altyapı dosyasını dahil etmesine uygulanan `infrastructure.env` ile); böylece E01 ve E02 aynı anda çalışabilir.

## Sınırlamalar (sonuçları kullanmadan önce okuyun)

- **Fiilen bir kez, yalnızca veritabanı etkisi için, yalnızca bu tüketici için.** Garanti, tüketicinin kendi PostgreSQL transaction'ı içindeki yazmalar için geçerlidir. Diğer tüketicilere, diğer veritabanlarına ya da dış çağrılara uzanmaz.
- **Exactly-once iddiası yok.** Her tüketici 4 iş emri için 6 teslimat aldı (yayımlanan 5 kopya, 1 yeniden teslim). Bu, en az bir kez teslimatın doğal davranışıdır; outbox'ın engelleyebileceği bir hata değildir.
- **İki çökme noktası da HTTP ile kurulur** ve süreç belleğinde yaşar. Gerçek bir çökme her yerde olabilir; bu iki nokta, iki tür tekrarı ürettikleri için seçildi.
- **Stok eksiye düşebilir** (`CHECK (on_hand >= 0)` yok); stok tükenmesi modellenmedi.
- **`consumer_deliveries` iş transaction'ının dışında** ve yalnızca kanıt için yazılır. Mekanizmanın parçası değildir.
- **Testlerdeki eşzamanlılık** ayrı bağlantılarla tek süreç içindedir; aynı kuyrukta birden fazla tüketici süreci değildir.
- **İşlenen ID'ler sonsuza kadar saklanır;** saklama süresi ödünleşimi için "Bu deney neyi göstermiyor" bölümüne bakın.
- **Simüle edilenler:** uygulama süreçlerinin SIGKILL ile sonlandırılması ve düzgün bir yeniden başlatma. Simüle edilmeyenler: veritabanı ya da broker çökmeleri, ağ bölünmeleri, sonucu bilinmeyen `COMMIT`'ler, saat sorunları.
- Kimlik bilgileri (`lab/lab`) ve arıza enjeksiyonu yalnızca yerel deney içindir.

## Kısa arıza analizi

Outbox, yayımlama yükümlülüğünü kalıcı hale getirerek "kaybolan olay" boşluğunu kapatır; ama yükümlülüğü yerine getirildi olarak işaretlemek, broker'ın kabulüyle birlikte commit edilemeyen ikinci bir yazmadır. Bu penceredeki her çökme yeniden yayıma, tüketicinin `COMMIT`'i ile ack'i arasındaki her çökme de yeniden teslime dönüşür. İkisi de broker'ın ya da outbox'ın hatası değildir; en az bir kez teslimatın anlamı budur. Broker yeniden yayımlanmış bir kopyayı yeni bir mesajdan ayıramaz; tekrarı tanıyabilecek tek yer tüketicidir ve bunu yalnızca kararlı bir iş kimliğiyle (`event_id`) yapabilir. Tüketici bu tanımayı yalnızca atomikliğe sahip olduğu yerde güvenilir kılabilir: kendi veritabanı transaction'ı içinde, kararı bir benzersizlik kısıtı verirken. Bu transaction'ın dışında aynı soru, dış etkileri gerçekleştiren sistemler tarafından yeniden yanıtlanmalıdır.
