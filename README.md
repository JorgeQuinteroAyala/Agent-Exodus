# Exodus.Agent

Agentes de monitoreo de salud de servicios para plataformas **Docker Swarm (RHEL/Linux)** e **IIS (Windows)**, publicando a un índice unificado de Elasticsearch.

Los dos agentes comparten modelos, lógica de Elastic, controllers HTTP, verificador de salud dual (interno/externo), filtros, política de retención y heartbeats a través del proyecto `Exodus.Agent.Core`. Cada host aporta únicamente la integración con su plataforma.

---

## Estructura de la solución

```
Exodus.Agent.sln
├── .gitlab-ci.yml              Pipeline completo (build → containerize → deploy)
├── .dockerignore
├── .gitignore
├── docker-compose/
│   ├── base.yml                Config común del servicio Swarm
│   └── production.yml          Override producción México
├── Exodus.Agent.Core/          Librería compartida (net10.0, cross-platform)
│   ├── Models/
│   │   ├── EstadoServicio.cs
│   │   ├── HealthCheck.cs
│   │   ├── HeartBeat.cs
│   │   └── ResultadoPruebaUrl.cs
│   ├── Services/
│   │   ├── BaseServicioMonitoreo.cs    Loop, retención, latido, historial
│   │   ├── DetectorSistemaOperativo.cs
│   │   ├── EstadoEjecucionAgente.cs
│   │   ├── FiltrosComunes.cs           ServiciosIgnorados + DominiosBloqueados
│   │   ├── InicializadorIndicesElastic.cs
│   │   ├── ResolverDnsPublico.cs       DNS raw-socket contra 8.8.8.8
│   │   └── VerificadorHealthHttp.cs    Dual interna/externa con telemetría
│   ├── Controllers/
│   └── Configuration/
├── Exodus.Agent.Docker/        Host para Docker Swarm / RHEL (net10.0)
│   ├── Services/
│   │   ├── ServicioMonitoreoSwarm.cs   Tasks, Traefik discovery, variantes
│   │   └── VerificadorDocker.cs
│   ├── Dockerfile              Multi-stage alpine (~90 MB final)
│   ├── Program.cs
│   └── appsettings.json
└── Exodus.Agent.IIS/           Host para IIS / Windows (net10.0-windows)
    ├── Models/
    │   └── SitioIIS.cs
    ├── Services/
    │   ├── DetectorSitiosIIS.cs
    │   ├── ServicioMonitoreoIIS.cs
    │   └── VerificadorIIS.cs
    ├── Program.cs
    └── appsettings.json
```

---

## Lógica de monitoreo

### Verificación dual de salud

Ambos agentes verifican cada servicio con **dos canales en paralelo**:

- **Interno** — usa `HttpClient` con bypass de validación de certificado. Apunta a las URLs internas (Docker: `url_health` + Traefik inferido; IIS: bindings sin host-header + bindings con host-header).
- **Externo** — usa `HttpClient` con bypass + **resolver DNS contra `8.8.8.8`** (raw UDP) en lugar del DNS del sistema, para probar como lo haría un cliente desde internet. Sólo aplica a URLs HTTPS.

Cada prueba individual se guarda como `ResultadoPruebaUrl` anidado dentro del `HealthCheck` del ciclo, con URL, tipo (`Interna`/`Externa`), código HTTP, tiempo de respuesta en ms y detalle del error si falló. Esto permite dashboards en Kibana con latencia por URL, p95 por dominio, etc.

### Descubrimiento de URLs

| Plataforma | Fuentes | Expansión |
|---|---|---|
| Docker Swarm | Etiqueta `url_health` (separable por `,`/`;`/espacio) + etiquetas Traefik (`traefik.http.routers.<name>.rule: Host(X)` con detección HTTPS via `.tls=true` o `entrypoints=https/websecure`) | Sí — cada URL genera hasta 4 variantes: http/https × root/`/health` |
| IIS | Bindings del sitio (sin host-header → local, con host-header → interna, HTTPS con cert → externa) | No — se prueban las URLs tal cual los bindings |

### Reglas de `Estado` del servicio

| Plataforma | Regla |
|---|---|
| Docker | Basado en réplicas: `Correcto` si `ReplicasRunning >= ReplicasDeseadas`, `Inestable` si hay pero faltan, `Critico` si no hay ninguna running. |
| IIS | Basado en código HTTP: `Critico` si alguna prueba retornó 5xx o todas las pruebas fallaron sin código (timeout / connection refused). `Desconocido` si el sitio no tiene URLs válidas. `Correcto` en cualquier otro caso (200-499). |

### Código HTTP aceptable (`Salud = Alcanzable`)

| Plataforma | Criterio |
|---|---|
| Docker | **200-299** (sólo success codes). Un 503 marca `No Alcanzable`. |
| IIS | **200-499** (el servidor está vivo aunque el endpoint responda 401/403/404). 5xx marca `No Alcanzable` y dispara `Estado=Critico`. |

### Filtros unificados

Los dos agentes comparten las mismas dos listas en `appsettings.json`:

- **`Agent:ServiciosIgnorados`** — omite servicios (Docker: por nombre del servicio Swarm; IIS: por nombre del sitio).
- **`Agent:DominiosBloqueados`** — elimina URLs que contengan cualquiera de estos substrings antes de probarlas.

### Nombre del nodo en el heartbeat

| Plataforma | Fuente |
|---|---|
| Docker | `docker.System.GetSystemInfoAsync` → `Swarm.InspectNodeAsync(NodeID)` → `Hostname`. Fallback a `Environment.MachineName` y a `$HOSTNAME`. **No** usa `MachineName` directo porque dentro del contenedor devolvería el Container ID. |
| IIS | `Environment.MachineName`. |

---

## Build y despliegue

### Docker / RHEL — automatizado vía GitLab CI

**Flujo:**

| Disparador | Qué pasa |
|---|---|
| push a `release/<n>` | `build` → `docker-build-image` (push imagen `$CI_REGISTRY_IMAGE:<n>`) |
| tag Git `vX.Y.Z` | `build` → `docker-build-prod-image` (manual) → `deploy-production-mexico` (manual, hace `docker stack deploy`) |

Pipeline self-contained. Runners requeridos: `development-env`, `contenedoresprod`.

### Docker / RHEL — manual

```bash
docker build -t exodus-agent:local -f Exodus.Agent.Docker/Dockerfile .

IMAGE_NAME=exodus-agent:local docker stack deploy \
  --with-registry-auth \
  -c docker-compose/base.yml \
  -c docker-compose/production.yml \
  exodus-agent
```

### Windows / IIS — publicación manual

```powershell
dotnet publish Exodus.Agent.IIS/Exodus.Agent.IIS.csproj `
  -c Release `
  -f net10.0-windows `
  -r win-x64 `
  --no-self-contained `
  -o bin\publish2
```

O desde Visual Studio con el `FolderProfile.pubxml` incluido.

---

## Configuración

### `appsettings.json` — comunes a ambos agentes

| Clave | Default |
|---|---|
| `Elastic:Url` | — |
| `Elastic:ServicesIndex` | `health-servicios-v2` |
| `Elastic:HeartbeatsIndex` | `heartbeats-agente` |
| `Agent:AgentId` | — |
| `Agent:IntervaloRevisionEnSegundos` | `60` |
| `Agent:HealthChecksRetencionEnHoras` | `48` |
| `Agent:ServiciosIgnorados` | `[]` |
| `Agent:DominiosBloqueados` | `[]` |

Overrides de producción: el `docker-compose/base.yml` inyecta `Elastic__*` y `Agent__*` vía variables de entorno (notación `__`).

### Exclusivo Docker
- `Docker:Endpoint` — default `unix:///var/run/docker.sock`.

### Exclusivo IIS
- `Agent:Criticidades` — diccionario `{ NombreSitio: Criticidad }`.

---

## Endpoints HTTP

| Método | Ruta | Descripción |
|---|---|---|
| `GET` | `/health` | Estado del agente: Elastic, plataforma, uptime, última ejecución exitosa. `200` si todo OK, `503` si degradado. |
| `POST` | `/api/actualizar` | Dispara un ciclo de monitoreo fuera de banda. Usa semáforo, no solapa con el ciclo programado. |

**HEALTHCHECK del contenedor Docker:** valida solamente que el servidor HTTP responda, no que `/health` retorne 200. Un `503 degraded` (Elastic caído) NO mata al agente — reiniciarlo no arregla nada. El `/health` sigue reportando el detalle real para observabilidad.

---

## Notas de migración

### Stack y servicio Docker
- Stack: `exodus-health-agent` → `exodus-agent`.
- Servicio: `exodus-health-agent-v2` → `exodus-agent`.
- DNS público: `exodus-health-agent-v2.apymsa-prdsvr.apymsa.com.mx` → `exodus-agent.apymsa-prdsvr.apymsa.com.mx`.

Antes del primer deploy nuevo:
```bash
docker stack rm exodus-health-agent
```

### IIS — renombrado de `SitiosIgnorados` → `ServiciosIgnorados`
Al actualizar el IIS, hay que editar el `appsettings.json` del servidor Windows donde está publicado el agente: renombrar la clave `Agent:SitiosIgnorados` a `Agent:ServiciosIgnorados`. Sin este cambio, el nuevo agente deja de respetar la lista de sitios ignorados.

### Variable de entorno corregida en el stack
El `base.yml` anterior tenía `Agent__RefreshIntervalSeconds=60`, nombre que no coincidía con el código. Ahora es `Agent__IntervaloRevisionEnSegundos`. Sin síntoma observable porque el default era el mismo valor, pero el bug latente queda corregido.

### Índices Elastic
- Se mantiene `health-servicios-v2` y `heartbeats-agente` en producción (datos existentes intactos).
- **Mapping nuevo incluye `PruebasUrls` anidado** dentro de `HealthChecks`. Si el índice `health-servicios-v2` ya existe en prod con el mapping viejo, Elasticsearch **rechazará** el primer documento con `PruebasUrls` porque no existe ese field en el mapping. Dos opciones:
  - **(A)** Borrar el índice antes del primer deploy del código nuevo → `DELETE /health-servicios-v2`. Pierdes historial pero quedas limpio.
  - **(B)** Actualizar el mapping con un `PUT /_mapping` añadiendo el nested `PruebasUrls`. Conservas historial. Te puedo armar el JSON exacto si eliges esta ruta.

### Endpoint `/health`
Mismo shape en ambos agentes con claves `plataforma` y `plataforma_estado`. El Docker antes usaba `docker` — ajustar cualquier monitor externo.

### Endpoint `/api/actualizar`
Ahora disponible en ambos agentes (antes sólo en RHEL).
