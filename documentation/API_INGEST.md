# SafeView API Ingest — push-based detekcje (CameraTransport.Api)

**Wersja API:** v1.0 · **Data:** 2026-04-26 · **Status:** GA

Ten dokument opisuje jak zewnętrzny system (kamera embedded, edge appliance, własny inference server) wysyła klatki + pre-computed detekcje do SafeView przez REST. SafeView nie polluje takich kamer — czeka na push i wpada od razu do swojego trigger/VLLM/action pipeline-u.

---

## Setup

### 1. Utwórz kamerę typu Api w UI

1. `/cameras` → **Dodaj kamerę**
2. **Vendor:** wybierz `API (push) — zewnętrzny sender`
3. Wpisz **Nazwę** (jedyne wymagane pole)
4. **Zapisz**

Po zapisie SafeView automatycznie tworzy:
- Pełnokadrową ROI (geometria nie jest edytowalna — sender już zrobił swoje wycięcia)
- Pełnokadrową Strefę (czworokąt obejmujący kadr)

Otwórz kamerę ponownie do edycji żeby zobaczyć **URL endpointu** + **przykłady curl**.

### 2. Wygeneruj klucz API

1. `/api-keys` → **Nowy klucz**
2. Scope: zaznacz `api:cameras:write`
3. **Utwórz** — skopiuj wygenerowany klucz (raz wyświetlony)

**Opcjonalnie — pin per kamera (defense-in-depth):**
W `/cameras` → edycja → pole **Przypisz do klucza API**: wklej `Id` klucza (z `/api-keys`). Tylko ten konkretny klucz będzie mógł pisać do tej kamery.

### 3. Dopnij triggery do auto-provisioned strefy

`/admin/triggers` → **Dodaj** → wybierz strefę "Pełna klatka" tej kamery, dopisz warunki (DetectionClass-based polecane, bo `ModelId` nie jest sensowne dla zewnętrznych detekcji).

---

## Endpoint

```
POST /api/v1/cameras/{cameraId}/ingest         — multipart (primary)
POST /api/v1/cameras/{cameraId}/ingest-json    — JSON-only (fallback)
```

**Auth:** `Authorization: Bearer <api_key>` (scope `api:cameras:write` wymagany).
**Rate limit:** 1800 req/min/kamera (30 fps × 60s) — sliding window per `cameraId`.

---

## Format JSON metadata

```json
{
  "schema_version": "1.0",
  "frame": {
    "width": 1920,
    "height": 1080,
    "captured_at": "2026-04-26T12:00:00.123Z",
    "frame_id": "abc-123-uuid"
  },
  "detections": [
    {
      "label": "person",
      "confidence": 0.92,
      "bbox": { "x": 100, "y": 200, "width": 50, "height": 100 },
      "track_id": "t-9921",
      "polygon": null,
      "keypoints": null,
      "attributes": { "helmet_color": "yellow" }
    }
  ],
  "source": {
    "name": "Frigate",
    "model": "yolov8n",
    "inference_ms": 12.3
  }
}
```

### Pola

| Pole | Typ | Wymagane | Opis |
|---|---|---|---|
| `schema_version` | string | nie (default `"1.0"`) | Forward-compat. |
| `frame.width` / `frame.height` | int | **tak** | Pixel dimensions klatki — używane do normalizacji bboxów. |
| `frame.captured_at` | ISO 8601 UTC | **tak** | Czas faktycznej obserwacji (od sendera). Server clamp-uje gdy &gt; ±5min od `now` (warning w `system_events`). |
| `frame.frame_id` | string (max 128) | nie | UUID dla idempotency. Retry z tym samym ID = 200 OK + `status: "already_processed"`. |
| `detections[].label` | string (max 80) | **tak** | Klasa obiektu. Match po `DetectionClass` (preferowane) albo bezpośrednio przez trigger conditions. |
| `detections[].confidence` | double 0..1 | **tak** | Pewność detekcji. |
| `detections[].bbox.x/y/width/height` | double | **tak** | **Pixel coords**, top-left origin. Server normalizuje do `[0..1]` względem `frame.width/height`. Musi mieścić się w klatce. |
| `detections[].track_id` | string | nie | Multi-frame tracking ID (forward-compat — pipeline obecnie ignoruje, ale przechowywane w detection metadata). |
| `detections[].polygon` | `[{x,y}, …]` | nie | Instance segmentation (forward-compat). |
| `detections[].keypoints` | `[{x,y,name?,confidence?}, …]` | nie | Pose estimation (forward-compat). |
| `detections[].attributes` | dict | nie | Sender-specific dane (kolor kasku, plate, …). Pipeline ignoruje, audit zachowuje. |
| `source.name` / `source.model` / `source.inference_ms` | — | nie | Metadata o source-ie (do logów/audytu). |

### Bbox — pixel coords vs normalized

API przyjmuje **pixel coords** (sender zwykle ma je z YOLO output). SafeView wewnętrznie pracuje na `[0..1]` — server normalizuje przez `x / width` i `y / height`. Walidacja: `bbox.x + bbox.width ≤ frame.width` (in-bounds).

### ModelId

Pipeline ustawia `ModelId="external"` dla wszystkich detekcji z ingest. Używaj triggerów z **DetectionClass-based conditions** (matchują po label, ignorują ModelId) zamiast legacy ModelId+Labels.

---

## Przykłady

### Multipart (primary)

```bash
curl -X POST 'https://safeview.example.com/api/v1/cameras/abc123/ingest' \
  -H 'Authorization: Bearer ghp_xxx_YOUR_API_KEY' \
  -F 'frame=@/tmp/frame.jpg;type=image/jpeg' \
  -F 'metadata={
    "schema_version": "1.0",
    "frame": { "width": 1920, "height": 1080, "captured_at": "2026-04-26T12:00:00Z", "frame_id": "evt-001" },
    "detections": [
      { "label": "person", "confidence": 0.92, "bbox": { "x": 100, "y": 200, "width": 50, "height": 100 } }
    ],
    "source": { "name": "Frigate", "model": "yolov8n" }
  };type=application/json'
```

### JSON-only (image as base64)

Dla cloud webhooków albo gdy sender nie radzi sobie z multipart.

```bash
curl -X POST 'https://safeview.example.com/api/v1/cameras/abc123/ingest-json' \
  -H 'Authorization: Bearer ghp_xxx_YOUR_API_KEY' \
  -H 'Content-Type: application/json' \
  -d '{
    "schema_version": "1.0",
    "image_base64": "/9j/4AAQSkZJRg...JPEG bytes...",
    "frame": { "width": 1920, "height": 1080, "captured_at": "2026-04-26T12:00:00Z" },
    "detections": [
      { "label": "person", "confidence": 0.92, "bbox": { "x": 100, "y": 200, "width": 50, "height": 100 } }
    ]
  }'
```

### JSON-only (image z URL)

Sender hostuje obraz na S3/MinIO — server pobiera (max 10MB, 5s timeout):

```json
{
  "schema_version": "1.0",
  "image_url": "https://my-storage.example.com/frames/2026/04/26/abc.jpg",
  "frame": { "width": 1920, "height": 1080, "captured_at": "2026-04-26T12:00:00Z" },
  "detections": [
    { "label": "person", "confidence": 0.92, "bbox": { "x": 100, "y": 200, "width": 50, "height": 100 } }
  ]
}
```

---

## Response

### 200 OK — accepted

```json
{
  "status": "accepted",
  "frame_id": "evt-001",
  "detections_count": 1,
  "processing_ms": 23
}
```

### 200 OK — duplicate (idempotency)

Gdy `frame_id` był już widziany w ostatnich 1000 frame-ach tej kamery:

```json
{
  "status": "already_processed",
  "frame_id": "evt-001",
  "detections_count": 0,
  "processing_ms": 1
}
```

### 400 Bad Request

- Brak `frame` (multipart) lub `image_base64`/`image_url` (JSON-only)
- Niepoprawny JSON metadata
- Bbox poza klatką (`x + width > frame.width`)
- `confidence` poza `[0, 1]`
- Image > 20 MB

### 401 Unauthorized

Brak / niepoprawny API key.

### 403 Forbidden

Camera ma `IngestApiKeyId` ustawione i to nie jest ten klucz.

### 404 Not Found

Kamera o tym ID nie istnieje.

### 400 Bad Request — wrong transport

```json
{ "error": "Camera transport is 'Rtsp', not 'Api' — cannot ingest" }
```

### 429 Too Many Requests

Przekroczony limit 1800 req/min/kamera.

### 500 Internal Server Error

Nie udało się zapisać klatki na dysku albo pipeline rzucił.

---

## Integracja z pipeline-em SafeView

Po przyjęciu request-u SafeView:

1. Zapisuje obraz w `storage/frames/{cameraId}/yyyy/MM/dd/HHmmss_uuid.jpg` (jak normalny snapshot)
2. Mapuje detekcje DTO → `DetectionResult` (z normalizacją pixel→[0..1], `ModelId="external"`)
3. Wywołuje `IDetectionPipeline.ProcessExternalDetectionsAsync(...)`
4. Pipeline pomija inferencję, przechodzi do `TriggerEvaluator` z dostarczonymi detekcjami
5. Reszta jak dla normalnej kamery: zone match → trigger fire → spatial filters → VLLM gate → `Incident` insert → `ActionDispatcher`
6. Live event `TriggerFired` na `/hubs/flow`, snapshot do `IDetectionSnapshotStore` dla `/monitor`

**Downstream nie wie że źródło jest zewnętrzne.** Incident ma `OccurredAt = frame.captured_at` (czas sendera), więc historia jest spójna mimo opóźnień sieciowych.

---

## Tracking ostatnich frame-ów (Monitor / Flow)

Strona `/monitor` pokazuje ostatnią klatkę + bboxy dla każdej kamery — Api kamera widoczna identycznie jak RTSP. Strona `/flow` emituje `TriggerFired` event przez SignalR — ten sam mechanism.

Jeśli sender przestanie pchać, strona `/monitor` pokaże ostatnią znaną klatkę i timestamp z `frame.captured_at`. Brak wbudowanego "stale" badge — follow-up.

---

## Limity i ograniczenia

- **Image size:** max 20 MB (multipart i base64-decoded)
- **JSON metadata size:** max 1 MB
- **Frame ID length:** max 128 znaków
- **Label length:** max 80 znaków
- **Rate limit:** 1800 req/min/kamera (30 fps sustained)
- **Idempotency window:** 1000 ostatnich `frame_id` per kamera (in-memory, reset przy restarcie)
- **Image URL fetch timeout:** 5s
- **Frame retention:** brak TTL automatycznego — pliki w `storage/frames/` żyją wiecznie. Recommended: cron + `find storage/frames -mtime +30 -delete`. Hosted-service follow-up.

---

## Bezpieczeństwo

- Endpoint wymaga API key z scope `api:cameras:write`
- Per-camera pin (`IngestApiKeyId`) blokuje cross-camera writes z innego klucza
- Rate-limit per kamera chroni pipeline przed flood-em
- Validation odrzuca: niepoprawne bbox bounds, confidence > 1, oversized payload, niepoprawny JSON
- Image URL fetch ograniczone do http(s) i 10MB (defense vs SSRF + DOS)
- Walidacja `cameraId` przez Mongo lookup (route value), brak path traversal
- Ścieżki frame storage budowane server-side (Guid filename), brak user-controlled paths

---

## Smoke test E2E

Po setupie:

```bash
# 1. Sanity check ping
curl -H 'Authorization: Bearer YOUR_KEY' \
  https://safeview.example.com/api/v1/ping

# 2. Wyślij testową klatkę z jedną detekcją
curl -X POST 'https://safeview.example.com/api/v1/cameras/YOUR_CAM_ID/ingest' \
  -H 'Authorization: Bearer YOUR_KEY' \
  -F 'frame=@test.jpg' \
  -F 'metadata={
    "frame":{"width":1920,"height":1080,"captured_at":"'$(date -u +%Y-%m-%dT%H:%M:%SZ)'"},
    "detections":[{"label":"person","confidence":0.95,"bbox":{"x":100,"y":100,"width":200,"height":400}}]
  };type=application/json'

# 3. Sprawdź /monitor — powinieneś zobaczyć kafel z bboxem
# 4. Sprawdź /flow — animacja TriggerFired (jeśli skonfigurowałeś trigger)
# 5. Sprawdź /incidents — nowy incident z OccurredAt = captured_at
```

---

## Future / follow-up

- Frame retention TTL hosted service (Ticket #50)
- Persistent idempotency store (Mongo TTL collection) zamiast in-memory (Ticket #51)
- Last-ingest counter widoczny w `/cameras` (wymaga update `Camera.LastSnapshotAt` z pipeline-u)
- Schema v1.1 — wymagane segmentation polygon dla wybranych klas
- Webhook outbound: SafeView pchający triggery do zewnętrznego systemu (odwrotny kierunek)
