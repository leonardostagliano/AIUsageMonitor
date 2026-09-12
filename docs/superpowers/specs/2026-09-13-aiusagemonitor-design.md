# AIUsageMonitor — Design

Data: 2026-09-13
Stato: approvato in brainstorming, in attesa di revisione della spec scritta

## 1. Obiettivo

App Windows sempre attiva che mostra in un unico punto, per ogni agente di coding AI installato sulla macchina:

- la quota consumata nelle finestre di rate limit (percentuale e orario di reset);
- lo stato in tempo reale delle sessioni aperte (al lavoro, attende input, finito, errore).

Ispirata ad [AgentBar](https://github.com/scari/AgentBar) (macOS, Swift, MIT), ma con due superfici Windows:

- un'icona nella **tray** con menu;
- un **notch laterale** sul bordo destro dello schermo, sempre in primo piano, collassato a una linguetta con un'indicazione per agente, che si espande al passaggio del mouse.

Agenti supportati nella versione 1: **Claude Code** e **OpenAI Codex**. L'architettura a provider permette di aggiungerne altri (Gemini, Copilot, Cursor) senza toccare UI e stato.

## 2. Non obiettivi (v1)

- Storico d'uso e grafici.
- Suoni personalizzati.
- Altri agenti oltre Claude Code e Codex.
- Rinnovo del token OAuth di Claude: l'app legge soltanto, il rinnovo lo fa Claude Code.
- Piattaforme diverse da Windows 10/11 x64.

## 3. Stack e vincoli

- .NET 10 SDK (10.0.401 già installato), C#, WPF. Build e test con la sola CLI `dotnet`, senza Visual Studio.
- Nessuna dipendenza nativa npm. Lo script hook usa Node (v22 già presente, runtime degli hook esistenti dell'utente).
- Nessun pacchetto NuGet di terze parti nell'app; `System.Text.Json` per il JSON. Test con xUnit.
- Icone da `@lobehub/icons-static-svg` 1.95.0 (MIT): `claude.svg` e `codex.svg`, versioni mono a `currentColor`, convertite in `PathGeometry` XAML con riempimento bianco. I marchi restano dei rispettivi titolari; il file `THIRD-PARTY-NOTICES.md` riporta licenza e provenienza.

## 4. Architettura

```
AIUsageMonitor.sln
src/
  AIUsageMonitor.Core/      libreria net10.0, nessuna dipendenza UI
    Models/                 Agent, UsageSnapshot, UsageWindow, SessionState, HookEvent, Settings
    Usage/                  IUsageProvider, ClaudeUsageProvider, CodexUsageProvider, UsageCache
    Hooks/                  HookEventReader (tail del file eventi), SessionTracker (macchina a stati),
                            HookInstaller (merge idempotente in settings.json / hooks.json)
    Infrastructure/         Paths, JsonlReader (lettura dalla coda), Clock, FileLogger
  AIUsageMonitor.App/       WPF net10.0-windows
    Tray/                   TrayIconController (NotifyIcon WinForms + menu), TrayIconRenderer
    Notch/                  NotchWindow, NotchViewModel, AgentCard, SessionRow, animazioni
    Settings/               SettingsWindow, SettingsViewModel
    Notifications/          ToastService
    Startup/                SingleInstance, AutoStart (registro Run)
    Assets/                 Icons.xaml (geometrie Claude/Codex), app.ico, hook.cjs
tests/
  AIUsageMonitor.Tests/     xUnit sul Core, fixture in tests/Fixtures/
docs/superpowers/specs/     questo documento
```

Flusso dati:

```
credentials.json  -> ClaudeUsageProvider -+
~/.codex/sessions -> CodexUsageProvider  -+-> UsageService (timer + cache) -> NotchViewModel -> NotchWindow
                                                                                   ^
hook.cjs -> events.jsonl -> HookEventReader -> SessionTracker ---------------------+-> TrayIconRenderer
                                                       +-> ToastService
```

Il Core espone eventi (`UsageUpdated`, `SessionsChanged`); l'App li proietta sul thread UI. Nessuna classe del Core conosce WPF.

## 5. Modelli

```csharp
enum AgentKind { Claude, Codex }

record UsageWindow(string Label,          // "5h", "7g", "7g Fable"
                   double Percent,        // 0..100
                   DateTimeOffset? ResetsAt,
                   Severity Severity);    // Normal, Warning, Critical (da API se presente, altrimenti soglie 50/80)

record UsageSnapshot(AgentKind Agent,
                     IReadOnlyList<UsageWindow> Windows,
                     string? PlanLabel,     // "Max 20x", "prolite"
                     string? ExtraUsage,    // "Extra 0/170 EUR" solo se abilitato e usato
                     UsageStatus Status,    // Ok, Stale, TokenExpired, NoData, Error
                     string? StatusMessage,
                     DateTimeOffset FetchedAt);

enum SessionPhase { Working, NeedsInput, Idle, Error }
// Idle = sessione aperta senza turno in corso. Etichetta UI: "finito" se ha già completato
// almeno un turno (Message valorizzato), altrimenti "pronto".

record SessionState(AgentKind Agent, string SessionId, string DisplayName, string? Cwd,
                    SessionPhase Phase, string? Message, DateTimeOffset LastEventAt, DateTimeOffset StartedAt);

record HookEvent(DateTimeOffset Ts, AgentKind Agent, string Event, string SessionId,
                 string? Cwd, string? NotificationType, string? Message, string? Source);
```

## 6. Provider quota

### 6.1 Claude Code

- Credenziali: `%USERPROFILE%\.claude\.credentials.json`, campo `claudeAiOauth` con `accessToken`, `expiresAt` (ms epoch), `subscriptionType`, `rateLimitTier`. Lettura sola, mai scrittura, mai log del token.
- Se `expiresAt` è passato o il file manca: nessuna chiamata, `Status = TokenExpired`, messaggio "Apri Claude Code per rinnovare la sessione". Il file viene riletto a ogni ciclo (Claude Code lo aggiorna quando rinnova).
- Chiamata: `GET https://api.anthropic.com/api/oauth/usage`, header `Authorization: Bearer <token>` e `anthropic-beta: oauth-2025-04-20`, timeout 10 s.
- Mappatura risposta (verificata il 2026-09-13 sull'account dell'utente):
  - `five_hour.utilization` / `resets_at` → finestra "5h";
  - `seven_day.utilization` / `resets_at` → finestra "7g";
  - ogni voce di `limits[]` con `kind = weekly_scoped` e `scope.model.display_name` → finestra "7g <Modello>" con `percent` e `severity`;
  - `extra_usage` → riga `ExtraUsage` se `is_enabled` e `used_credits > 0`;
  - piano da `subscriptionType` + `rateLimitTier` (es. "Max 20x").
- Errori: 401 → `TokenExpired`; altri HTTP o rete → `Stale` con gli ultimi valori validi e `StatusMessage` "Ultimo aggiornamento hh:mm".
- Intervallo di refresh: 60 s (configurabile 30–600 s).

### 6.2 Codex

- Sorgente: `%USERPROFILE%\.codex\sessions\YYYY\MM\DD\rollout-<ts>-<id>.jsonl`.
- Selezione file: tutti i JSONL modificati negli ultimi 7 giorni, ordinati per data di modifica decrescente.
- Parsing: si legge ogni file dalla coda a blocchi, cercando record `type = event_msg`, `payload.type = token_count` con `payload.rate_limits`. Per ogni `limit_id` si conserva l'ultimo record incontrato (il più recente). La lettura si ferma quando tutti i file recenti sono stati visti o dopo 200 file.
- Per ogni `rate_limits`: `primary` e `secondary` (se presenti) diventano finestre con etichetta derivata da `window_minutes` (< 1440 → "Nh", altrimenti "Ng"), `used_percent` e `resets_at` (epoch secondi). Se `resets_at` è nel passato si avanza di `window_minutes` finché è nel futuro e la percentuale si azzera (finestra già ruotata). `plan_type` → `PlanLabel`.
- Nessun file o nessun `rate_limits` → `Status = NoData`, messaggio "Nessuna sessione Codex recente".
- Trigger: `FileSystemWatcher` sulla cartella sessioni (debounce 2 s) più fallback periodico ogni 30 s.

### 6.3 Cache

`%LOCALAPPDATA%\AIUsageMonitor\usage-cache.json` con l'ultimo snapshot valido per agente. All'avvio le barre si popolano dalla cache (marcate `Stale`) finché non arriva il primo aggiornamento.

## 7. Stato live via hook

### 7.1 Script hook

`%USERPROFILE%\.aiusagemonitor\hook.cjs` (copiato dall'app all'installazione, sovrascritto se la versione incorporata è più nuova). Invocazione: `node "<path>\hook.cjs" claude` oppure `codex`. Legge tutto lo stdin, estrae i campi, appende una riga a `%USERPROFILE%\.aiusagemonitor\events.jsonl` e termina con exit 0 in ogni caso (mai bloccare l'agente). Salta gli eventi con `agent_id` valorizzato (subagenti Claude).

Riga evento:

```json
{"ts":"2026-09-13T10:15:02.123Z","agent":"claude","event":"Notification","session_id":"abc","cwd":"C:\\Users\\...\\AIUsageMonitor","notification_type":"permission_prompt","message":"Bash needs approval","source":null}
```

`message` è troncato a 200 caratteri. Rotazione: se il file supera 5 MB l'app lo rinomina in `events.1.jsonl` (una sola generazione) dopo averlo letto.

### 7.2 Eventi registrati

| Agente | File | Eventi |
|---|---|---|
| Claude Code | `~/.claude/settings.json` | `SessionStart`, `UserPromptSubmit`, `Notification`, `Stop`, `StopFailure`, `SessionEnd` |
| Codex | `~/.codex/hooks.json` | `SessionStart`, `UserPromptSubmit`, `Stop`, `SessionEnd` |

Non si registra `PermissionRequest` per non interferire con il flusso dei permessi: `Notification` con `notification_type = permission_prompt` copre il caso.

### 7.3 Installer

- `HookInstaller.Install(agent)`: legge il file di configurazione, per ogni evento aggiunge un gruppo `{ "hooks": [ { "type": "command", "command": "node \"<path>\\hook.cjs\" <agent>", "timeout": 5 } ] }` solo se nessun comando esistente contiene la sottostringa `aiusagemonitor/hook.cjs` (confronto con separatori normalizzati). Prima di scrivere crea `~/.aiusagemonitor/backups/<nomefile>.<yyyyMMdd-HHmmss>.bak`. Riscrive il JSON con indentazione a 2 spazi preservando tutte le altre chiavi.
- `HookInstaller.Remove(agent)`: elimina solo i gruppi il cui unico comando è quello dell'app; se un gruppo contiene anche altri comandi rimuove solo la voce propria. Backup anche qui.
- `HookInstaller.Status(agent)`: `Installed`, `Partial` (alcuni eventi mancanti), `NotInstalled`, `ConfigMissing`.
- Per Codex l'installer verifica anche che `config.toml` contenga `hooks = true` e, se manca, lo segnala senza modificarlo.

### 7.4 Macchina a stati (per coppia agente + session_id)

| Evento | Transizione |
|---|---|
| `SessionStart` | crea la sessione in `Idle` senza `Message` (etichetta "pronto"); `source = resume` mantiene lo `StartedAt` se già nota |
| `UserPromptSubmit` | → `Working` |
| `Notification` con `permission_prompt`, `idle_prompt`, `agent_needs_input`, `elicitation_dialog`, `elicitation_url_dialog` | → `NeedsInput`, `Message` = messaggio |
| `Notification` di altro tipo | nessuna transizione |
| `Stop` | → `Idle` (etichetta "finito"), `Message` = ultime 120 battute di `last_assistant_message`, oppure "Turno completato" se assente |
| `StopFailure` | → `Error`, `Message` = motivo |
| `SessionEnd` | rimuove la sessione |

Regole aggiuntive:

- Nome visualizzato: ultimo segmento di `cwd`. Per Codex, se l'hook non fornisce `cwd`, lo si ricava dal record `session_meta` del file `rollout-*-<session_id>.jsonl` (cache in memoria per id). Fallback: primi 8 caratteri dell'id.
- Sessioni senza eventi da 12 ore vengono rimosse (scansione ogni 5 minuti).
- All'avvio si rilegge la coda di `events.jsonl` (ultime 24 ore) per ricostruire lo stato, applicando anche i `SessionEnd`.
- Lettura incrementale: offset persistito in memoria, `FileSystemWatcher` sul file con debounce 200 ms e fallback ogni 2 s.

### 7.5 Stato aggregato per agente

Usato dalla linguetta del notch e dalla tray. Priorità: `Error` > `NeedsInput` > `Working` > `Idle` > nessuna sessione.

## 8. Interfaccia

### 8.1 Tray

- `NotifyIcon` WinForms (assembly `System.Windows.Forms` referenziato dal progetto WPF con `UseWindowsForms`). Nessun pacchetto aggiuntivo.
- Icona: glifo bianco dell'app in 16/32 px con un pallino in basso a destra disegnato a runtime (GDI+) nel colore dello stato aggregato peggiore tra gli agenti; tooltip con riepilogo "Claude 5h 48% · Codex 7g 90%".
- Click sinistro: fissa aperto il notch (toggle). Doppio click: apre le impostazioni.
- Menu destro: Aggiorna ora · Mostra/Nascondi notch · Installa hook (sottomenu Claude/Codex con stato) · Avvio automatico (check) · Impostazioni… · Esci.

### 8.2 Notch

- Finestra WPF: `WindowStyle=None`, `AllowsTransparency=true`, `Topmost=true`, `ShowInTaskbar=false`, stile tool window (non compare in Alt+Tab), `ResizeMode=NoResize`. DPI awareness `PerMonitorV2`.
- Posizione: ancorata al bordo destro del monitor scelto (default: monitor principale), verticalmente centrata con offset configurabile in pixel. Riposizionata su cambio risoluzione o monitor (`SystemEvents.DisplaySettingsChanged`).
- Collassato: larghezza 28 px, altezza 12 + 36 × numero agenti abilitati. Per agente: icona bianca 18 px e, sotto, un pallino 8 px dello stato aggregato. Angoli sinistri arrotondati 10 px, sfondo `#1B1B1F` al 92 % di opacità, bordo 1 px bianco al 12 %.
- Espanso: larghezza 320 px, altezza in base al contenuto (massimo 80 % dello schermo, poi scroll). Animazione della larghezza 150 ms ease-out. Si espande all'ingresso del mouse nella linguetta, si richiude 400 ms dopo l'uscita dal bordo della finestra. Click sulla linguetta o click sinistro sulla tray lo fissa aperto (icona puntina); secondo click o `Esc` lo sblocca.
- Card agente: intestazione con icona, nome ("Claude Code", "Codex"), badge piano; una riga per finestra con etichetta, barra a colori (verde < 50 %, ambra 50–80 %, rosso > 80 %, oppure `severity` dell'API), percentuale e countdown al reset ("2h 10m", "3g 4h"); riga extra usage se presente; riga di stato se `Stale`, `TokenExpired`, `NoData` o hook non installati (link "Installa"); elenco sessioni con pallino, nome, fase, tempo dall'ultimo evento.
- Colori dei pallini: `Working` verde `#3FB950` con pulsazione lenta; `NeedsInput` ambra `#D29922`; `Idle` grigio pieno `#8B8B93`; `Error` rosso `#F85149`; nessuna sessione grigio contorno.
- Il notch non ruba il focus (`WS_EX_NOACTIVATE`) e non è click-through: i click servono per fissare e per i link.

### 8.3 Impostazioni

Finestra WPF standard con sezioni:

- Agenti: abilita/disabilita Claude e Codex, intervallo di refresh per ciascuno.
- Notch: monitor, offset verticale, ritardo di chiusura, dimensione (normale/compatta).
- Hook: stato per agente, pulsanti Installa/Rimuovi, percorso del file eventi, pulsante "Apri cartella".
- Notifiche: attiva per evento (attende input, finito, errore) e per agente.
- Sistema: avvio automatico con Windows, apri cartella log.

Le impostazioni vivono in `%LOCALAPPDATA%\AIUsageMonitor\settings.json` e si applicano senza riavvio.

## 9. Notifiche

- `NotifyIcon.ShowBalloonTip` (Windows le mostra come toast native). Titolo "<Agente> · <sessione>", testo = messaggio dell'evento o "Turno completato" / "Errore API".
- Emesse su transizione a `NeedsInput`, `Idle` (solo da `Working`, cioè turno completato) ed `Error`, con dedupe per (sessione, fase, messaggio) e finestra minima di 3 s tra toast dello stesso agente.
- Click sulla toast: fissa aperto il notch.

## 10. Sistema

- Istanza singola tramite mutex `Local\AIUsageMonitor`; un secondo avvio fissa aperto il notch dell'istanza attiva (segnale via `EventWaitHandle`).
- Avvio automatico: valore `AIUsageMonitor` in `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` con il percorso dell'exe corrente.
- Log su file `%LOCALAPPDATA%\AIUsageMonitor\logs\app-<yyyyMMdd>.log`, 7 giorni conservati, livello Info; mai token né contenuti dei prompt.
- Publish: `dotnet publish src/AIUsageMonitor.App -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true` produce un exe singolo che richiede il runtime .NET 10 Desktop. Variante `--self-contained true` documentata nel README.

## 11. Gestione errori

| Situazione | Comportamento |
|---|---|
| Token Claude scaduto/mancante | card con "Apri Claude Code per rinnovare", barre grigie con ultimi valori |
| Rete assente / HTTP diverso da 200 | valori in cache, stato `Stale` con orario ultimo aggiornamento |
| Nessuna sessione Codex | "Nessuna sessione Codex recente" |
| Hook non installati | riga nella card con link "Installa hook" |
| File di configurazione hook non parsabile | installer si ferma, nessuna scrittura, messaggio con percorso del file |
| JSONL corrotto o riga incompleta | riga ignorata, parsing continua |
| Eccezione non gestita | loggata, app resta attiva; il timer riparte al ciclo successivo |

## 12. Test

xUnit su `Core`, fixture reali sanificate in `tests/Fixtures/`:

- `ClaudeUsageProviderTests`: mappatura completa della risposta (5h, 7g, limiti per modello, extra usage), 401, token scaduto, rete assente con cache.
- `CodexUsageProviderTests`: selezione dei file, ultimo `rate_limits` per `limit_id`, etichette da `window_minutes`, avanzamento di `resets_at` scaduto, assenza dati.
- `SessionTrackerTests`: tutte le transizioni della tabella 7.4, dedupe, rimozione per inattività, ricostruzione all'avvio, risoluzione nome Codex da `session_meta`.
- `HookInstallerTests`: su un `settings.json` con hook preesistenti (Herdr, KB, toast) installa una sola volta, non duplica alla seconda esecuzione, rimuove solo le proprie voci, crea il backup, non scrive se il JSON è invalido. Stesso set per `hooks.json` Codex.
- `HookEventReaderTests`: lettura incrementale, righe incomplete, rotazione.
- `hook.cjs`: test Node minimale (`node --test`) con stdin simulato per Claude e Codex.

Verifica manuale della UI a ogni milestone: avvio, linguetta, espansione, fissaggio, menu tray, impostazioni, toast.

## 13. Milestone

1. Scaffold solution, Core con modelli, provider Claude e Codex, test verdi, console di prova che stampa gli snapshot.
2. App WPF: tray, notch collassato/espanso con quote reali, impostazioni base.
3. Hook: script, installer con test, tracker sessioni, pallini di stato, notifiche.
4. Rifinitura: autostart, istanza singola, cache, log, publish, README e notice licenze.

## 14. Repository

- Repo personale dell'utente su GitHub (account `lstagliano`), autore dei commit `stagliano.leonardo@icloud.com` (già configurato a livello globale).
- Nessun push automatico: l'utente pubblica manualmente.
- Nessun riferimento ad assistenti AI nei commit o nei file versionati.
- Artefatti di lavoro generati (piani, handoff, `.superpowers/`) esclusi via `.git/info/exclude`.
- Convenzione commit: `feat:`, `fix:`, `test:`, `docs:`, `chore:`.
