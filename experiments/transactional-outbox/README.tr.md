# E01 — Lost Event (transactional outbox)

**Türkçe** · [English](README.md)

İlgili yazı: [Transactional Outbox](https://emregorgulu.com/tr/makaleler/transactional-outbox/) ([English](https://emregorgulu.com/en/articles/transactional-outbox/)).

> **Durum:** Çalıştırılabilir. Aşağıdaki deney betiği ve test paketi, burada listelenen sabit sürümlerle 2026-10-22 tarihinde çalıştırıldı. Başka makinelerde elde edilecek sonuçlar için bir iddiada bulunulmuyor.

## Soru

Veritabanı transaction'ı commit edilir ama ilgili olay hiç yayımlanmazsa ne olur? Transactional outbox bu boşluğu kapatır mı?

Deney, aynı küçük `WorkOrders` servisindeki iki yazma yolunu karşılaştırır:

| Mod | Ne yapar | Etiket |
| --- | --- | --- |
| `naive` | İş emrini PostgreSQL'de `COMMIT` eder, **ardından** ayrı bir adımda `WorkOrderCreated` olayını RabbitMQ'ya yayımlar (çift yazma). | **Güvensiz başlangıç senaryosu.** Yalnızca arızayı göstermek için var. |
| `outbox` | İş emrini **ve** değiştirilemez outbox kaydını **aynı** transaction içinde yazar. Arka plandaki publisher bekleyen kayıtları publisher confirm ile RabbitMQ'ya iletir, ardından yayımlandı olarak işaretler. | Önlem |

Mod `E01_MODE` ile seçilir ve varsayılan değeri yoktur. Servis bu değişken olmadan başlamayı reddeder.

## Hızlı başlangıç (tek komut)

Docker ve Compose v2 yeterli. Bu klasörde:

```bash
docker compose up --build
```

Bu komut PostgreSQL'i, RabbitMQ'yu ve **iki** yazma yöntemini yan yana başlatır:

| Adres | Servis | Yöntem |
| --- | --- | --- |
| http://localhost:18481 | `outbox-api` | transactional outbox (önlem) |
| http://localhost:18482 | `naive-api` | çift yazma (**güvensiz** başlangıç) |
| http://localhost:15682 | RabbitMQ yönetim arayüzü (kullanıcı `lab`, şifre `lab`) | yayımlanan olaylar `e01.work-order-created` kuyruğunda |

Her iki API'de bir iş emri oluşturun:

```bash
curl -X POST localhost:18481/work-orders -H 'X-Operation-Id: outbox-ok'
curl -X POST localhost:18482/work-orders -H 'X-Operation-Id: naive-ok'
```

Şimdi her iki süreci `COMMIT`'ten hemen sonra sonlandırın (yalnızca yerel deney; süreç öldüğü için istek yanıt almaz):

```bash
curl -X POST localhost:18482/work-orders -H 'X-Operation-Id: naive-crash'  -H 'X-Fault-Inject: crash-after-commit'
curl -X POST localhost:18481/work-orders -H 'X-Operation-Id: outbox-crash' -H 'X-Fault-Inject: crash-after-commit'
```

İki container da, bir orkestratörün yapacağı gibi kendiliğinden yeniden başlar (`restart: on-failure`). Veritabanını broker ile karşılaştırın:

```bash
docker compose exec postgres psql -U lab -d lab -c "
  SELECT w.operation_id, w.write_mode,
         CASE WHEN o.event_id IS NULL THEN '-' WHEN o.published_at IS NULL THEN 'pending' ELSE 'published' END AS outbox
  FROM work_orders w LEFT JOIN outbox_messages o ON o.aggregate_id = w.id ORDER BY w.created_at;"

docker compose exec rabbitmq rabbitmqctl -q list_queues name messages
```

22 Ekim 2026'da gözlemlenen: dört iş emri commit edildi, ancak kuyrukta **üç** olay var. `naive-crash` olayı hiç oluşmadı; `COMMIT` ile yayım arasında kayboldu. `outbox-api` ise yeniden başladıktan sonra `outbox-crash` kaydını hâlâ bekler durumda buldu ve yaklaşık yarım saniye içinde yayımladı. Hangi işlemlerin broker'a ulaştığını görmek için yönetim arayüzünde kuyruğu açıp *Get messages* kullanın (her mesajda `operation_id` bulunur).

Her şeyi verilerle birlikte durdurup silmek için:

```bash
docker compose down -v
```

Aşağıdaki deney betiği ve otomatik testler aynı konuyu daha kapsamlı ele alır (broker kesintisi, rollback, doğrulamalar).

## Klasör yapısı

```text
experiments/transactional-outbox/
├── compose.yaml              # "drlab-e01" projesi: `docker compose up` → outbox-api + naive-api; script/test profilleri
├── Dockerfile                # build / test / runtime aşamaları (.NET SDK yalnızca Docker içinde)
├── run-e01.sh                # A → B → C deney betiği; kanıtları yazdırır, ortamı kaldırır
├── test-e01.sh               # xUnit paketini Docker içinde çalıştırır, ortamı kaldırır
├── TransactionalOutbox.slnx
├── src/WorkOrders/
│   ├── Program.cs            # minimal API: POST /work-orders, GET /health
│   ├── WorkOrderService.cs   # iki yazma yolu + arıza enjeksiyon noktaları
│   ├── OutboxPublisher.cs    # sahiplenme (FOR UPDATE SKIP LOCKED) → confirm ile yayım → işaretleme
│   ├── Broker.cs             # publisher confirm ve mandatory=true ile RabbitMQ publisher'ı
│   ├── FaultInjection.cs     # X-Fault-Inject başlığı, SIGKILL ile sonlandırma
│   ├── schema.sql            # work_orders, outbox_messages, değiştirilemezlik trigger'ı
│   └── ...
└── tests/WorkOrders.Tests/   # gerçek PostgreSQL + RabbitMQ'ya karşı xUnit v3 kabul testleri
```

## Kullanılan sürümler

| Bileşen | Sürüm |
| --- | --- |
| .NET SDK / ASP.NET Core runtime imajları | `mcr.microsoft.com/dotnet/sdk:10.0.402`, `mcr.microsoft.com/dotnet/aspnet:10.0.13` |
| PostgreSQL | `postgres:18.6-alpine` |
| RabbitMQ | `rabbitmq:4.2.9-management-alpine` |
| NuGet | Npgsql 10.0.3, RabbitMQ.Client 7.2.2, xunit.v3 3.2.2, xunit.runner.visualstudio 3.1.5, Microsoft.NET.Test.Sdk 18.11.0 |

## Ön koşullar

- Compose v2.20 veya üstü içeren Docker (`include` desteği için). macOS (arm64) üzerinde Docker 28.4 / Compose 2.39 ile denendi.
- Host üzerinde `curl` ve `jq` (yalnızca `run-e01.sh` kullanıyor).
- Yerel .NET SDK gerekmez. Her şey konteynerlerde derlenir ve çalışır.
- Boş loopback portları: `127.0.0.1:18480` (API) ve `127.0.0.1:15682` (RabbitMQ yönetim arayüzü/API'si). `DRLAB_E01_API_PORT` ve `DRLAB_RABBITMQ_UI_PORT` ile değiştirilebilir. PostgreSQL host'a port açmaz. Bunun yerine `docker compose exec postgres psql -U lab -d lab` kullanın.

`drlab-e01` compose proje adı, konteyner, ağ ve volume'ları makinedeki diğer yerel ortamlardan ayrı tutar.

## Çalıştırma

Depo kök dizininden:

```bash
# Deney betiği: temiz ortam → A → B → C → özet → kaldırma (ilk build'den sonra ~30 sn)
./experiments/transactional-outbox/run-e01.sh

# Ortamı sonrasında açık bırakmak için (RabbitMQ arayüzü: http://127.0.0.1:15682, lab/lab)
KEEP=1 ./experiments/transactional-outbox/run-e01.sh
# işiniz bitince
docker compose -f experiments/transactional-outbox/compose.yaml down -v

# Kabul testleri (xUnit, Docker içinde, compose PostgreSQL/RabbitMQ'ya karşı), ardından kaldırma
./experiments/transactional-outbox/test-e01.sh

# Betik olmadan eşdeğeri
docker compose -f experiments/transactional-outbox/compose.yaml run --rm --build tests
docker compose -f experiments/transactional-outbox/compose.yaml --profile test down -v
```

Herhangi bir gözlem beklentiden farklı çıkarsa `run-e01.sh` sıfırdan farklı bir çıkış koduyla sonlanır.

### Elle çalıştırma

```bash
C="docker compose -f experiments/transactional-outbox/compose.yaml"
$C up -d --wait postgres rabbitmq
# veya E01_MODE=outbox
E01_MODE=naive $C up -d --no-deps workorders

curl -X POST http://127.0.0.1:18480/work-orders -H 'X-Operation-Id: op-1'
curl -X POST http://127.0.0.1:18480/work-orders -H 'X-Operation-Id: op-2' -H 'X-Fault-Inject: crash-after-commit'

$C exec postgres psql -U lab -d lab -c 'SELECT * FROM work_orders'
$C exec postgres psql -U lab -d lab -c 'SELECT operation_id, event_id, published_at FROM outbox_messages'
$C exec rabbitmq rabbitmqctl list_queues name messages
$C logs --no-log-prefix workorders | jq -c 'select(.phase) | {ts, phase, operation_id, event_id}'
$C down -v
```

## Arıza enjeksiyon noktaları

Arıza enjeksiyonunun tamamı yalnızca yerel deney içindir. İstek başlığı yalnızca `E01_FAULT_INJECTION_ENABLED=true` olduğunda (`compose.yaml` içinde ayarlı) dikkate alınır. Aksi halde API başlığı sessizce yok saymaz, `400` döner.

| Tetikleyici | Tam olarak nerede | Etki |
| --- | --- | --- |
| `X-Fault-Inject: crash-after-commit` | `COMMIT` döndükten hemen sonra, başka hiçbir adım çalışmadan önce (`WorkOrderService.CrashAfterCommitIfRequested`) | Süreç kendine **SIGKILL** gönderir: `finally` blokları çalışmaz, düzgün kapanış olmaz. Çıkış kodu 137 olur. İstemci kopan bir bağlantı görür. Docker'da servis `init: true` ile çalıştığı için sinyal PID 1 kuralı nedeniyle yok sayılmaz. Önce log satırları boşaltılır; `COMMIT` ile sonlandırma arasında olan tek şey budur. |
| `X-Fault-Inject: fail-before-commit` | INSERT'lerden sonra, `COMMIT`'ten önce | Transaction rollback edilir. API, denenen `work_order_id` / `event_id` ile `500` döner; böylece bu kimliklerin hiçbir yerde bulunmadığını doğrulayabilirsiniz. |
| `docker compose stop rabbitmq` | Broker | Broker erişilemez olur. Outbox yazmaları kabul edilmeye devam eder. Publisher `broker_unavailable` loglar, bekleme süresini artırır (1 sn'den başlayıp iki katına çıkarak en fazla 10 sn) ve kayıtları beklemede bırakır. |
| `E01_OUTBOX_PUBLISHER_ENABLED=false` | Outbox publisher | O süreçte publisher çalışmaz ("publisher durduruldu"). Publisher'ı açık bir süreç başlayana kadar kayıtlar beklemede kalır. |

## Betik ne yapıyor, ne gözlemlendi

Bir çalıştırmanın tam çıktısı uzundur. Aşağıdaki kesitler 2026-10-22 tarihli son çalıştırmadan kısaltılarak alındı. Kimlikler ve zaman damgaları her çalıştırmada değişir.

### A. Başlangıç senaryosu (naif çift yazma, GÜVENSİZ): COMMIT sonrası sonlandırma

1. Servis `naive` modda başlatılır. `op-naive-ok` kontrol isteği commit edilir ve yayımlanır (`201`, `event_status: "published"`).
2. `crash-after-commit` ile `op-naive-crash`: `curl: (52) Empty reply from server`, konteyner `exited (exit code 137)`.

**Beklenen:** İş emri var, olayı yok.
**Gözlenen:**

```text
  operation_id  | write_mode | outbox_entry | event_in_broker
----------------+------------+--------------+------------------
 op-naive-ok    | naive      | (none)       | yes
 op-naive-crash | naive      | (none)       | NO  <-- no event

    queue e01.work-order-created: 1 message(s)
      message_id=01a16299-eb6e-7c9f-823f-281182da09df  type=WorkOrderCreated  operation_id=op-naive-ok  work_order_id=01a16299-eb6e-...

    07:53:36.960  info     db_committed      op-naive-crash   01a16299-ebbf-76ae-83aa-90b1ffa7dff4
    07:53:36.960  warn     fault_injected    op-naive-crash   01a16299-ebbf-76ae-83aa-90b1ffa7dff4  fault=crash-after-commit
    07:53:36.960  critical process_killed    op-naive-crash   01a16299-ebbf-76ae-83aa-90b1ffa7dff4  fault=crash-after-commit
```

`op-naive-crash` için `publish_started` satırı yoktur.

Sistemde bir olay borcu olduğunu hatırlayan hiçbir şey kalmaz: yayımlama niyeti yalnızca sonlandırılan sürecin belleğindeydi. Servisi yeniden başlatmak onu geri getirmez. Bu boşluğu ancak yukarıdaki uzlaştırma tablosu gibi, veritabanı ile broker durumunu dışarıdan karşılaştıran bir kontrol fark edebilir.

### B. Önlem (transactional outbox): broker kapalı, COMMIT sonrası sonlandırma, yeniden başlatma

1. RabbitMQ durdurulur. Servis `outbox` modda başlatılır.
2. `op-outbox-broker-down` kabul edilir (`201`, `event_status: "pending"`). Publisher kaydı sahiplenir ve başarısız olur: `broker_unavailable attempt=1 retry_in_ms=1000 BrokerUnreachableException`.
3. `crash-after-commit` ile `op-outbox-crash`: çıkış kodu 137. Sonlandırma noktası A ile aynıdır.
4. Yeniden başlatmadan önce: `published_at` değeri NULL olan iki outbox kaydı vardır ve broker kapalıdır.
5. RabbitMQ başlatılır: kuyrukta hâlâ yalnızca A'dan kalan tek mesaj vardır. Ardından WorkOrders yeniden başlatılır.

**Beklenen:** Yeniden başlatma ve broker'ın geri gelmesinden sonra bekleyen iki kayıt yayımlanır ve işaretlenir.
**Gözlenen:**

```text
--- B6. Evidence BEFORE restart   (event_type sütunu çıkarıldı; broker: STOPPED)
     operation_id      |               event_id               |          created_at           | published_at
-----------------------+--------------------------------------+-------------------------------+--------------
 op-outbox-broker-down | 01a16299-f9da-7cfb-baa6-139e6e98373d | 2026-10-22 07:53:40.572677+00 |
 op-outbox-crash       | 01a16299-fa81-7e34-839b-669207234905 | 2026-10-22 07:53:40.73752+00  |

--- B8. Restart WorkOrders; wait until no outbox row is pending
    pending outbox rows: 2 -> 0 (after 1 check(s) at 0.5 s intervals)

--- B9. Evidence AFTER restart
     operation_id      |               event_id               |          created_at           |         published_at
-----------------------+--------------------------------------+-------------------------------+-------------------------------
 op-outbox-broker-down | 01a16299-f9da-7cfb-baa6-139e6e98373d | 2026-10-22 07:53:40.572677+00 | 2026-10-22 07:53:48.034373+00
 op-outbox-crash       | 01a16299-fa81-7e34-839b-669207234905 | 2026-10-22 07:53:40.73752+00  | 2026-10-22 07:53:48.034373+00

    queue e01.work-order-created: 3 message(s)
      message_id=01a16299-eb6e-7c9f-...  operation_id=op-naive-ok
      message_id=01a16299-f9da-7cfb-baa6-139e6e98373d  operation_id=op-outbox-broker-down
      message_id=01a16299-fa81-7e34-839b-669207234905  operation_id=op-outbox-crash

--- B10. Reconciliation (database vs broker)
     operation_id      | write_mode | outbox_entry | event_in_broker
-----------------------+------------+--------------+------------------
 op-naive-ok           | naive      | (none)       | yes
 op-naive-crash        | naive      | (none)       | NO  <-- no event
 op-outbox-broker-down | outbox     | published    | yes
 op-outbox-crash       | outbox     | published    | yes
```

Teslim edilen her mesajın RabbitMQ `message_id` değeri, outbox'taki `event_id` ile aynıdır. Yeniden başlatılan süreç sırasıyla `outbox_claimed count=2` → `broker_connected` → `publish_confirmed` (iki kez) → `outbox_marked_published count=2` loglar.

### C. Rollback: COMMIT öncesi hata

`fail-before-commit` ile gönderilen `op-outbox-rollback`, `attempted_work_order_id` ve `attempted_event_id` alanlarıyla `500` döner.

**Beklenen:** İki kimlik de hiçbir yerde yoktur ve olay yayımlanmaz.
**Gözlenen:** `work_orders = 0`, `outbox_rows = 0`. Publisher'a outbox'ı yoklaması için iki saniye tanındıktan sonra da kuyruk derinliği 3'te kalır. Loglar: `fault_injected` → `db_rolled_back`.

### Betiğin yazdırdığı özet

```text
A. naive baseline
  OK   process killed by SIGKILL after COMMIT (exit code)             expected=137 observed=137
  OK   op-naive-crash work order committed                            expected=1 observed=1
  OK   op-naive-crash WorkOrderCreated in broker (lost event)         expected=0 observed=0
  OK   op-naive-ok control event in broker                            expected=1 observed=1
B. transactional outbox
  OK   process killed by SIGKILL after COMMIT (exit code)             expected=137 observed=137
  OK   (a) outbox work orders without an outbox entry                 expected=0 observed=0
  OK   outbox rows pending before restart                             expected=2 observed=2
  OK   (b) outbox rows pending after restart + broker restored        expected=0 observed=0
  OK   (b) outbox events that reached the broker                      expected=2 observed=2
C. rollback
  OK   (c) op-outbox-rollback work orders                             expected=0 observed=0
  OK   (c) op-outbox-rollback outbox rows                             expected=0 observed=0
  OK   (c) op-outbox-rollback events in broker                        expected=0 observed=0
  OK   queue depth unchanged by the rolled-back request               expected=3 observed=3
```

## Kanıtlar nasıl okunur

- **Uzlaştırma tablosu.** `work_orders` tablosunu `outbox_messages` ile ve broker kuyruğunda bulunan `work_order_id` değerleriyle birleştirir. `NO  <-- no event`, broker'da olayı bulunmayan commit edilmiş bir iş emrini gösterir. `PENDING`, yayımlama niyeti kalıcı hale gelmiş ama henüz iletilmemiş bir olayı gösterir.
- **Kuyruk derinliği**, doğrudan kuyruk sürecine soran `rabbitmqctl list_queues` ile alınır. Yönetim API'sindeki mesaj sayıları belirli aralıklarla örneklenir ve saniyelerce geride kalabilir. Bu yüzden betik yönetim API'sini yalnızca mesajlara **göz atmak** için kullanır (`ackmode: ack_requeue_true`; mesajlar kuyrukta kalır).
- **Kanıt kuyruğu.** `e01.work-order-created`, `e01.work-orders` exchange'ine bağlı kalıcı bir kuyruktur ve sonraki tüketicinin yerini tutar. Onu kimse tüketmez; broker'a gerçekten ulaşan her olay burada birikir. Mesajlar `mandatory=true` ile yayımlanır, yani yönlendirilemeyen bir mesaj başarı değil, başarısız yayım sayılır.
- **Loglar** her satırda tek bir düz JSON nesnesidir. Alanlar: `ts`, `level`, `category`, `phase`, `mode`, `operation_id` (`X-Operation-Id` başlığından gelir; kayıtlarda da saklanır ve AMQP `correlation_id` olarak gönderilir), `work_order_id`, `event_id` (`outbox_messages.event_id` olarak saklanır, AMQP `message_id` olarak gönderilir), `count`, `attempt`, `retry_in_ms`, `fault`, `msg` ve `error`.

| `phase` | Anlamı |
| --- | --- |
| `startup` | Mod ve ayarlar. Naif modda bir `warn` satırı `UNSAFE BASELINE` der. |
| `db_committed` | `COMMIT` döndü. Outbox modda ayrıca `outbox_status=pending` yazar. |
| `fault_injected`, `process_killed` | Enjekte edilen arıza, ardından SIGKILL. |
| `db_rolled_back` | COMMIT öncesi hata senaryosundaki transaction rollback edildi. |
| `publish_started`, `publish_confirmed`, `publish_failed` | Broker'a yayım ve onayı (ya da başarısızlığı). |
| `outbox_claimed`, `outbox_marked_published` | Publisher `count` adet bekleyen kaydı sahiplendi, ardından `count` kaydı yayımlandı olarak işaretledi. |
| `broker_connected`, `broker_unavailable` | Broker bağlantısı kuruldu ya da kurulamadı (`attempt` ve `retry_in_ms` ile). |

Örnek log satırları (kısaltılmış):

```json
{"ts":"…","level":"info","category":"WorkOrders.WorkOrderService","mode":"naive","operation_id":"op-naive-crash","work_order_id":"01a16299-ebbf-…","event_id":"01a16299-ebbf-76ae-…","phase":"db_committed","msg":"phase=db_committed Work order committed; event is NOT yet published (separate step)"}
{"ts":"…","level":"critical","category":"WorkOrders.ProcessTerminator","mode":"naive","operation_id":"op-naive-crash","work_order_id":"…","event_id":"…","phase":"process_killed","fault":"crash-after-commit","msg":"phase=process_killed fault=crash-after-commit Killing process with SIGKILL now"}
{"ts":"…","level":"warn","category":"WorkOrders.OutboxPublisherWorker","phase":"broker_unavailable","attempt":1,"retry_in_ms":1000,"msg":"phase=broker_unavailable attempt=1 retry_in_ms=1000 Outbox rows remain pending","error":"BrokerUnreachableException: None of the specified endpoints were reachable"}
```

## Otomatik testler

`tests/WorkOrders.Tests`, compose'daki gerçek PostgreSQL ve RabbitMQ'ya karşı çalışır. Ayrı bir veritabanı (`e01_tests`) ve ayrı exchange/kuyruk adları kullandığı için betiğin ürettiği duruma hiç dokunmaz. Çökme testleri gerçek servisi **ayrı bir işletim sistemi süreci** olarak başlatır ve SIGKILL ile sonlandırır. Yeniden başlatma, yepyeni bir süreçtir.

| Test | Ölçüt |
| --- | --- |
| `Baseline_naive_kill_after_commit_leaves_committed_work_order_without_event` | Başlangıç senaryosundaki arıza: commit edilmiş 2 naif iş emri, broker'da 1 olay; sonlandırılan istek için olay yok. |
| `A_every_committed_work_order_has_exactly_one_durable_outbox_entry` | (a) 40 eşzamanlı outbox yazması, dörtte biri COMMIT öncesi hata alır. Commit edilen 30 iş emrinin her biri tam olarak bir eşleşen outbox kaydına sahiptir; sahipsiz kayıt yoktur. |
| `A_B_outbox_entry_survives_kill_after_commit_and_is_published_after_restart_once_broker_is_reachable` | (a) + (b) Broker erişilemez (port 1) ve publisher başarısız olur. Sonlandırmadan sonra 3 kayıt bekler. Broker erişilebilirken başlatılan yeni süreç üçünü de yayımlar; `message_id` değerleri `event_id` değerleriyle aynıdır. |
| `C_rolled_back_work_order_creates_no_outbox_event` | (c) COMMIT öncesi hatadan sonra denenen kimlikler iki tabloda da yoktur. Bir publisher turu hiçbir şey yayımlamaz. Kuyruk boştur. |
| `Outbox_rows_are_immutable_except_a_single_published_at_transition` | Şemadaki trigger, payload güncellemesini ve ikinci kez `published_at` değişikliğini reddeder. |

`./experiments/transactional-outbox/test-e01.sh` çıktısı (2026-10-22; uygulamanın kendi log satırları çıkarıldı):

```text
  Passed WorkOrders.Tests.LostEventTests.Outbox_rows_are_immutable_except_a_single_published_at_transition [83 ms]
  Passed WorkOrders.Tests.LostEventTests.Baseline_naive_kill_after_commit_leaves_committed_work_order_without_event [355 ms]
  Passed WorkOrders.Tests.LostEventTests.C_rolled_back_work_order_creates_no_outbox_event [16 ms]
  Passed WorkOrders.Tests.LostEventTests.A_every_committed_work_order_has_exactly_one_durable_outbox_entry [46 ms]
  Passed WorkOrders.Tests.LostEventTests.A_B_outbox_entry_survives_kill_after_commit_and_is_published_after_restart_once_broker_is_reachable [946 ms]

Test Run Successful.
Total tests: 5
     Passed: 5
```

Testlerin gerçekten ayırt edici olduğunu doğrulamak için paket, kasıtlı olarak bozulmuş iki varyanta karşı da çalıştırıldı: biri outbox INSERT'ünü atlıyor, diğeri enjekte edilen hatada rollback yerine commit ediyor. Her biri ilgili testleri başarısız kıldı (5 testten 3'ü ve 5 testten 2'si).

## Publisher tasarımı

1. Tek bir transaction içinde `SELECT … WHERE published_at IS NULL ORDER BY created_at, event_id LIMIT 10 FOR UPDATE SKIP LOCKED` çalıştırılır. Eşzamanlı publisher'lar birbirinin kayıtlarını atlar. Bir publisher ölürse transaction'ı iptal olur, satır kilitleri kalkar ve kayıtlar yeniden beklemede kalır. Lease sütununa ya da bayat sahiplenmeleri temizleyen ayrı bir işe gerek yoktur.
2. Her kayıt publisher confirm ile yayımlanır (`BasicPublishAsync`, `basic.ack` geldikten sonra döner; nack, return veya 5 sn zaman aşımı istisna fırlatır).
3. Onaylanan kayıtlar için `UPDATE … SET published_at = now()` çalıştırılır, ardından `COMMIT` edilir. Bir yayım batch'in ortasında başarısız olursa o ana kadar onaylanan kayıtlar yine işaretlenir, geri kalanlar beklemede kalır.

Ödünleşim: publisher broker onaylarını beklerken transaction açık kalır, bu yüzden batch'ler küçük tutulur. Lease tabanlı sahiplenme (`claimed_until`) uzun transaction'lardan kaçınır ama daha fazla hareketli parça getirir.

## Sınırlamalar (sonuçları alıntılamadan önce okuyun)

- **Exactly-once yok.** Outbox **en az bir kez yayım** sağlar. Broker bir mesajı onaylar ve süreç kaydı işaretlemeden ölürse, ya da onay kaybolur veya zaman aşımına uğrarsa, aynı `event_id` yeniden yayımlanır. Tüketicilerin deduplikasyon yapması gerekir; bu **E02**'nin konusudur. Bu deney o aralığa arıza enjekte etmez. Testler broker'daki tekrarları kabul eder (*farklı* `message_id` değerlerini karşılaştırır) ve tekrar olmadığını iddia etmez.
- **Dış yan etkiler kapsam dışıdır.** Yalnızca veritabanı yazması ve broker'a yayım ele alınır. E-posta, üçüncü taraf HTTP çağrıları gibi etkiler bu transaction tarafından korunmaz.
- **Beklemede olmak teslim edilmek değildir.** Outbox modunda `event_status: "pending"` ile dönen `201`, yayımlama *niyetinin* kalıcı olduğunu söyler; olayın birine ulaştığını söylemez. Yayım gecikmesi yoklama aralığına (500 ms) ve bekleme süresine (en fazla 10 sn) bağlıdır.
- **İstemcinin belirsizliği çözülmez.** Sonlandırmadan sonra istemci kopan bir bağlantı görür ve iş emrinin commit edilip edilmediğini bilemez. Körlemesine bir yeniden deneme ikinci bir iş emri oluşturur. Bu bir idempotency sorusudur, outbox sorusu değil.
- **Sıralama**, tek bir publisher için en iyi çaba esaslıdır (`created_at, event_id`). Yeniden denemeler ve eşzamanlı publisher'lar olayların sırasını değiştirebilir.
- **Neler simüle ediliyor:** uygulama sürecinin SIGKILL ile sonlandırılması ve erişilemeyen broker (durdurulmuş konteyner ya da testlerde bağlantıyı reddeden port). Simüle edilmeyenler: PostgreSQL çökmesi, sonucu bilinmeyen `COMMIT` (`COMMIT` sırasında bağlantının kopması), ağ bölünmeleri, broker diskinin kaybı, çok düğümlü RabbitMQ veya quorum kuyrukları.
- **Başlangıç senaryosunun ikinci bir arıza yolu var** ve betik bunu çalıştırmıyor: naif modda broker kapalıyken `COMMIT` başarılı olur, yayım başarısız olur. API `503` döner ve iş emrinin yine olayı yoktur. Naif modda `docker compose stop rabbitmq` ile deneyebilirsiniz.
- **Uygulanmayanlar:** yayımlanmış outbox kayıtlarının saklama/temizleme politikası, `LISTEN/NOTIFY` ile uyandırma, metrikler.
- Kimlik bilgileri (`lab/lab`) ve arıza enjeksiyonu yalnızca yerel deneyler içindir.

## Kısa arıza analizi

Başlangıç senaryosu olayı kaybeder, çünkü yayımlama kararı birlikte commit edilemeyen iki yerde yaşar: PostgreSQL'in kalıcı durumu ve sürecin geçici belleği. İkisinin arasındaki bir çökme, yükümlülüğün hiçbir izini bırakmaz; yeniden denenecek bir şey kalmaz. Outbox bu yükümlülüğü durum değişikliğiyle aynı atomik commit'e taşır. O noktadan sonra çökme, durdurulmuş publisher ya da erişilemeyen broker yayımı yalnızca **geciktirir**; toparlanma, bekleyen kayıtları bulan bir yeniden başlatmadır. Bedeli, arızanın "kaybolan olay"dan "tekrarlanabilen olay"a dönüşmesidir. E02 bu yüzden bir sonraki adım.
