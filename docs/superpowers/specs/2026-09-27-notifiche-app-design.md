# Notifiche precise e con la grafica dell'app — Design

Data: 2026-09-27
Estende: [2026-09-13-aiusagemonitor-design.md](2026-09-13-aiusagemonitor-design.md) (sezione 9, Notifiche) e
[2026-09-24-grafica-premium-design.md](2026-09-24-grafica-premium-design.md) (palette e componenti)

## 1. Problemi

1. **Falso "attende input" dopo ogni turno.** Claude Code manda `Notification` con `notification_type = idle_prompt`
   ("Claude is waiting for your input") quando il prompt resta fermo per 60 s dopo la fine di un turno. Il tracker lo
   tratta come `NeedsInput` e la tray manda un secondo toast "Attenzione · …" circa 61 s dopo quello di completamento:
   l'utente legge "Claude ha bisogno di qualcosa" quando ha solo finito.
2. **Testo generico.** `permission_prompt` porta sempre "Claude needs your permission", anche quando Claude fa una
   domanda (`AskUserQuestion`), chiede di approvare un piano (`ExitPlanMode`) o quando il permesso lo chiede un agente
   di un workflow in background. Il toast non dice quale strumento né quale comando.
3. **Grafica.** I toast nativi di Windows non hanno niente dell'app oltre al logo: né la palette, né l'agente, né lo
   stato; restano nel Centro notifiche e non spariscono quando non servono più.

## 2. Fonti verificate

Dati reali di questa macchina, 13–27 settembre 2026 (`~/.aiusagemonitor/events.jsonl`, log dell'app):

- **`idle_prompt`**: 235 eventi, tutti con il messaggio "Claude is waiting for your input", sempre 60–63 s dopo lo
  `Stop` della stessa sessione. Dei 323 toast inviati dal 24 al 27 settembre, 60 sono questi falsi allarmi, in coppia
  con il toast di completamento di 61 s prima.
- **`permission_prompt`**: 252 eventi, tutti con il messaggio generico "Claude needs your permission"; 153 toast
  dal 24 al 27. Claude Code lo manda circa 7 s dopo che il `tool_use` in attesa e' stato scritto nel transcript:
  il `tool_use` senza `tool_result` e' quindi gia' leggibile quando arriva la notifica. Verificato su 5 casi:
  `AskUserQuestion` (domanda) e `Bash` con il comando; in un caso nessun `tool_use` era in sospeso nel transcript
  principale mentre girava un workflow in background (permesso di un suo agente).
- **`PostToolUse`** e' registrato solo con matcher `AskUserQuestion`: dopo un `permission_prompt` indica la risposta
  a una domanda. Concedere un permesso non manda alcun hook (lo copre il registro, spec 2026-09-24 §3).
- Il resto dei toast viene da `Stop`/`SubagentStop` (completamenti), `StopFailure`, dalle sessioni del registro e del
  cloud, e dagli avvisi dell'app (aggiornamento, "Terminale non trovato", hook installati).

## 3. Decisioni

| Tema | Decisione |
|---|---|
| `idle_prompt` | Nessuna notifica; la sessione resta "finito", non "attende input". |
| Posizione | Le card escono dalla linguetta del notch, sul bordo destro, alla sua altezza. |
| Card | Compatta: una riga di messaggio, clic per andare al terminale, ✕ per chiudere. Nessun bordo colorato. |
| Toast di Windows | Eliminati del tutto, anche per l'aggiornamento disponibile. |
| Non disturbare / schermo intero | Le card aspettano; all'uscita compaiono solo quelle ancora valide. |
| Suono | Due suoni propri dell'app: uno morbido per Finito, uno piu' marcato per permessi, piani, domande ed errori; avvisi dell'app muti; interruttore in Impostazioni, attivo di default. |

## 4. Precisione

### 4.1 `idle_prompt` (`SessionTracker`)

- `idle_prompt` non porta piu' a `NeedsInput`. Se la sessione e' `Working` e non ha agenti ne' workflow in corso
  (lo `Stop` si e' perso), passa a `Idle` in silenzio: il cambiamento porta `Silent = true` e non genera card. In
  ogni altro caso l'evento e' ignorato (nessun `SessionChange`).
- `AwaitsPrompt` sparisce da `SessionState` insieme alla logica che lo leggeva (aggregato della tray e della
  linguetta, `SubagentStart` che risveglia una sessione in attesa del prompt). `NeedsInput` resta solo per permessi,
  domande, piani, richieste MCP e agenti che attendono input.
- `SessionChange` guadagna `bool Silent` (default `false`).

### 4.2 Dettaglio dell'attesa (`AttentionDetail`)

Nuovo record in Core: `AttentionDetail(AttentionKind Kind, string? Tool, string? Summary, bool Background)` con
`AttentionKind { Permission, Question, Plan, Input }`.

`TranscriptAttentionReader.Read(transcriptPath)` legge al massimo gli ultimi 256 KB del transcript
(`ReverseLineReader`), riga per riga (un messaggio dell'assistente puo' occupare piu' righe con lo stesso id), e trova
l'ultimo `tool_use` senza un `tool_result` successivo con lo stesso id. Righe malformate si saltano; file assente,
bloccato o illeggibile → `null`.

| `tool_use` in sospeso | Kind | Tool | Summary |
|---|---|---|---|
| `AskUserQuestion` | Question | — | testo della prima domanda, `(+N)` se sono piu' d'una |
| `ExitPlanMode` | Plan | — | prima riga non vuota del piano, senza `#` |
| `Bash`, `PowerShell` | Permission | nome | prima riga del comando, spazi compressi |
| `Edit`, `Write`, `MultiEdit`, `NotebookEdit`, `Read` | Permission | nome | percorso del file, relativo alla `cwd` se ci sta sotto |
| `WebFetch` / `WebSearch` | Permission | nome | URL senza query / testo della ricerca |
| `Agent`, `Task` | Permission | nome | `description` |
| `mcp__<server>__<tool>` | Permission | `<tool>` | `<server>` |
| altro | Permission | nome | "Vuole usare <nome>" |

Ogni Summary e' tagliato a 120 caratteri. Il testo (comandi compresi) si mostra solo nella card: mai nei log.

**Risoluzione nel pump.** `HookEvent` guadagna `AttentionDetail? Attention`. `HookEventPump`, solo per gli eventi
live (non durante il replay silenzioso delle ultime 24 h), prima di `tracker.Apply`:

1. `Notification` Claude con `permission_prompt`, `elicitation_dialog`, `elicitation_url_dialog`, `agent_needs_input`
   o `worker_permission_prompt`, e gli eventi `Notification` sintetizzati dal registro (`waiting`): legge il transcript
   dell'evento o, se manca, quello gia' noto della sessione.
2. Se non c'e' un `tool_use` in sospeso nel transcript principale, prova i transcript degli agenti in esecuzione della
   sessione (`ClaudeAgentTranscriptLocator`) e prende il `tool_use` in sospeso piu' recente: `Background = true`. Una
   chiamata `Agent`/`Task` in sospeso nel transcript principale conta come "niente in sospeso" finche' la sessione ha
   agenti in esecuzione: resta aperta per tutta la vita dell'agente, e il permesso vero e' quello che aspetta lui.
3. Senza risultati: `permission_prompt` e `worker_permission_prompt` → `Permission` senza Tool (con `Background =
   true` se la sessione ha agenti o workflow in corso); `elicitation_*` → `Question` con Summary = messaggio
   dell'evento; `agent_needs_input` → `Input` con `Background = true`.

`SessionTracker` copia `Attention` sullo stato quando entra in `NeedsInput` e lo azzera in ogni altra fase. Le sessioni
del cloud in `requires_action` usano `Input` con il messaggio di `pending_action` (gia' in `CloudSessions`).

**Notch.** L'etichetta di fase di una sessione in `NeedsInput` diventa "permesso", "domanda", "piano da approvare" o
"attende input" secondo `Attention.Kind` (oggi e' sempre "attende input").

### 4.3 Durata del turno

`SessionState` guadagna `DateTimeOffset? TurnStartedAt`, l'inizio dell'ultimo turno: impostato all'ora dell'evento
quando la sessione entra in `Working` da `Idle`, `Error` o da nuova (`UserPromptSubmit`, `SubagentStart` su una
sessione `Idle`); **non** cambia quando torna `Working` da `NeedsInput` (stesso turno, dopo un permesso) ne' in
`Idle`/`Error`, cosi' la card lo legge sullo stato del `SessionChange`. La durata e' `LastEventAt - TurnStartedAt`
nel formato `42s`, `4m 12s`, `1h 03m`; la card "Finito" mostra `Finito · 4m 12s`, oppure solo "Finito" senza
`TurnStartedAt` (sessioni nate dal replay o dal registro gia' al lavoro).

## 5. Le card

### 5.1 Tipi

| Tipo | Quando | Etichetta (riga 2) | Colore | Messaggio (riga 3) | Chiusura automatica |
|---|---|---|---|---|---|
| Finito | `Idle` da `Working`, non `Silent` | `Finito · <durata>` | `SuccessText` | ultimo messaggio dell'assistente o "Turno completato" | 8 s |
| Permesso | `NeedsInput`, Kind `Permission` | `Permesso · <Tool>`, `Permesso · agente in background` | `WarningText` | Summary | no |
| Piano | `NeedsInput`, Kind `Plan` | `Piano da approvare` | `WarningText` | Summary | no |
| Domanda | `NeedsInput`, Kind `Question`/`Input` | `Domanda`, `Un agente attende input` | `Focus` | Summary o messaggio | no |
| Errore | `Error` | `Errore` | `DangerText` | messaggio dell'errore o "Errore API" | no |
| Avviso dell'app | `AppServices.Notice`, aggiornamenti | titolo dell'avviso | `TextMuted` (Info), `WarningText`, `DangerText` | testo dell'avviso | 6 s (Info), no (Warning/Error) |

Le impostazioni esistenti restano: `NotifyNeedsInput` (etichetta "Permessi e domande") copre Permesso, Piano e Domanda;
`NotifyTurnCompleted` → Finito; `NotifyError` → Errore; `NotifyClaude`/`NotifyCodex` filtrano per agente. Gli avvisi
dell'app si mostrano sempre. Codex non ha hook `Notification`: per Codex arrivano solo Finito ed Errore.

### 5.2 Aspetto

- Larga 340 DIP, superficie `Card`, `CornerRadius` 14, bordo `NotchBorder` da 1, ombra morbida (blur 24, opacita'
  0,45), alone dell'agente (`ClaudeGlow`/`CodexGlow`) nell'angolo in alto a sinistra. **Nessun bordo o barra laterale
  colorati.**
- Avatar da 30 DIP: il cerchio dell'agente come nella card del notch; per gli avvisi dell'app il logo dell'app.
- Riga 1: nome della sessione (`DisplayName`, con il prefisso `cloud ·`/`app ·`/`routine ·` se non e' un terminale)
  in `TextPrimary` 13 SemiBold con ellissi; a destra l'eta' ("ora", "2 min", "1 h") in `TextMuted` 11 (AA su `Card`), aggiornata
  ogni 30 s; ✕ visibile solo al passaggio del mouse.
- Riga 2: puntino da 7 DIP ed etichetta 11 SemiBold nel colore del tipo.
- Riga 3: messaggio in `TextMuted` 12, una riga con ellissi; tooltip con il testo completo.
- Card con chiusura automatica: barra da 2 DIP sul bordo inferiore (gradiente del tono `ToneNormalBrush` per Finito,
  `TextDisabled` per gli avvisi) che si svuota; il passaggio del mouse sopra la pila ferma tutti i timer.
- Tutti i colori vengono da `Theme.xaml`; i rapporti di contrasto dei nuovi abbinamenti testo/superficie entrano in
  `ColorContrastTests`.

### 5.3 Interazione

- Clic su una card di sessione: `AppServices.FocusTerminalAsync(session)` come il nome della riga nel notch (terminale,
  pagina cloud o app desktop), poi la card si chiude. Se fallisce compare l'avviso "Terminale non trovato" / "Pagina
  della sessione non aperta" (come oggi, ma come card).
- Clic su un avviso: "aggiornamento disponibile" apre la conferma di aggiornamento; gli altri fissano il notch.
- ✕: chiude la card. Lo stato che l'ha generata viene ricordato: la stessa card non torna finche' la sessione non
  cambia stato (stessa regola di dedupe di oggi, chiave fase + dettaglio).
- La finestra non prende mai il focus (`ShowActivated=False`, `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`) e non compare
  nella taskbar ne' in Alt+Tab.

### 5.4 Ciclo di vita (`NotificationBoard`, Core, puro)

- **Una card per sessione.** Un nuovo stato della stessa sessione sostituisce il contenuto della card esistente (senza
  animazione d'ingresso) e la porta in cima alla pila, perche' e' la notizia piu' recente.
- **Ritiro.** Una card Permesso/Piano/Domanda si ritira quando la sessione esce da `NeedsInput`; una card Errore
  quando torna `Working`/`NeedsInput`; una card Finito quando parte un nuovo turno o scade. `SessionEnd` ritira
  qualsiasi card della sessione. Quando il nuovo stato e' a sua volta notificabile (es. un secondo permesso, o
  `Working` → `Idle` a fine turno) la card della sessione viene sostituita sul posto invece di ritirarla. "Finito"
  nasce solo da `Working` → `Idle`: `NeedsInput` → `Idle` (permesso negato, turno chiuso) ritira la card e basta.
- **Avvisi dell'app.** Ogni avviso e' una card a se'; un avviso identico (titolo e testo) gia' visibile viene solo
  rinnovato (eta' e timer ripartono).
- **Pila.** Massimo 3 card visibili, la piu' recente in cima; le altre attendono e salgono quando si libera un posto;
  sotto la pila una pillola "+N altre" (clic: fissa il notch). Le 3 visibili si scelgono prima tra le card senza
  chiusura automatica (Permesso, Piano, Domanda, Errore, avvisi Warning/Error), dalla piu' recente, poi tra le altre:
  un "Finito" non spinge mai fuori un permesso in attesa.
- Il board e' guidato da `IClock` e riceve intenti (`Show`, `Retire`, `Dismiss`, `Tick`, `SetQuiet`); produce l'elenco
  ordinato delle card visibili e il numero di quelle in attesa. Nessuna dipendenza da WPF.

`NotificationComposer` (Core, puro) sostituisce `ToastService.Describe`: da `SessionChange` + `AppSettings` produce
l'intento per il board (`Show` con il modello della card, `Retire`, oppure niente).

## 6. Posizione e movimento

- **Una sola finestra host** (`NotificationHostWindow`): trasparente, `Topmost`, larga 340 + i margini dell'ombra,
  alta quanto la pila. Scartate: una finestra per card (piu' HWND, impilamento e riposizionamento fragili) e le card
  dentro `NotchWindow` (ne complicherebbe larghezza e animazioni).
- **Ancoraggio.** Stesso monitor e stesso `VerticalOffset` del notch. Il bordo destro della pila sta 8 DIP a sinistra
  della linguetta (larghezza della linguetta dalla modalita' compatta), o 8 DIP a sinistra del pannello quando il notch
  e' aperto o fissato, o 8 DIP dal bordo dell'area di lavoro quando il notch e' nascosto. La pila e' centrata
  verticalmente sul centro della linguetta e tenuta dentro l'area di lavoro.
- `NotificationPlacement.Compute(...)` (Core, puro, sul modello di `NotchPlacement`) calcola `Left`/`Top` in DIP da area
  di lavoro in pixel, scala DPI, rientro destro, altezza della pila e offset; la finestra si riposiziona ai cambi di
  display, DPI, impostazioni del notch e apertura/chiusura del pannello. `INotchHost` espone quanto serve (monitor,
  offset, larghezza occupata a destra) con un evento di cambiamento.
- **Movimento.** Ingresso: da 24 DIP a destra (da sotto la linguetta) con dissolvenza, 220 ms, `CubicEase` out.
  Uscita: 160 ms verso destra con dissolvenza. Le card rimaste scorrono al nuovo posto in 180 ms. Con
  `MotionSettings` ridotti: solo dissolvenza, nessuno scorrimento.

## 7. Non disturbare e schermo intero

- `QuietModeProbe.IsQuiet()`:
  - `SHQueryUserNotificationState` restituisce `QUNS_BUSY`, `QUNS_RUNNING_D3D_FULL_SCREEN`, `QUNS_PRESENTATION_MODE` o
    `QUNS_APP` → silenzio;
  - "Non disturbare" di Windows 11: stato WNF `WNF_SHEL_QUIETHOURS_ACTIVE_PROFILE_CHANGED` letto con
    `NtQueryWnfStateData` (valore diverso da 0 → silenzio). E' un'interfaccia non documentata: si verifica su questa
    macchina (Windows 11 26200) durante l'implementazione; se la lettura fallisce si considera "non in silenzio" e lo
    si scrive una volta nel log.
- In silenzio il board mette in coda invece di mostrare; il probe viene interrogato a ogni nuova card e ogni 2 s
  finche' c'e' una coda. All'uscita compaiono solo le card ancora valide (Permesso, Piano, Domanda, Errore non
  ritirati); Finito e avvisi Info in coda si scartano.

## 8. Suono

- **Due suoni dell'app**, file WAV PCM 16 bit mono 44,1 kHz in `src/AIUsageMonitor.App/Assets/Sounds/`, generati in modo
  deterministico da `tools/sounds/generate-sounds.js` (Node, nessuna dipendenza; lo script e i WAV sono nel repo, nessun
  suono di terzi):
  - `done.wav` (Finito): due note sinusoidali ascendenti, Mi5 659,26 Hz poi Si5 987,77 Hz, la seconda che parte 110 ms
    dopo la prima; ciascuna con attacco lineare di 8 ms e decadimento esponenziale (costante 90 ms), 2ª armonica al 15 %;
    durata totale 520 ms con coda a zero; picco −14 dBFS. Morbido, non chiede attenzione.
  - `attention.wav` (Permesso, Piano, Domanda, Errore): due tocchi brillanti Si5 987,77 Hz e Mi6 1318,51 Hz, 85 ms
    ciascuno con 55 ms di pausa, attacco 4 ms e decadimento esponenziale (costante 45 ms), 2ª armonica al 25 %; durata
    totale 420 ms; picco −10 dBFS. Piu' marcato, si riconosce senza guardare.
- `NotificationSound.Play(NotificationSoundKind kind)` li riproduce da risorsa incorporata con `PlaySound` di winmm
  (`SND_MEMORY | SND_ASYNC`), da un buffer caricato una volta per suono e fissato in memoria per tutta la vita del
  processo (con `SND_ASYNC` winmm lo legge dopo il ritorno della chiamata: un array gestito non fissato potrebbe essere
  spostato dal GC): passano dalla sessione audio dell'app, quindi seguono volume di sistema e mixer per app. Qualsiasi errore e' ignorato (una volta nel log, solo il tipo).
- `NotificationSoundKind { None, Done, Attention }`: il composer assegna `Done` a Finito, `Attention` a Permesso, Piano,
  Domanda ed Errore, `None` agli avvisi dell'app; con `NotifySound` spento tutto e' `None`.
- Il board restituisce il suono da riprodurre per ogni operazione (`Attention` prevale su `Done` se nella stessa
  operazione ne servirebbero due): mai in silenzio; all'uscita dal silenzio un solo `Attention` se era in coda almeno
  una card con suono `Attention` (le card `Done` in coda si scartano). Una card sostituita sul posto suona solo se il
  suo `StateKey` e' cambiato; un avviso rinnovato non suona.
- Impostazione `NotifySound` (default `true`), interruttore "Suono" nella scheda Notifiche.

## 9. Rimozione dei toast di Windows

- Via `AppNotificationSender`, `NotificationPayload` (e `NotificationPayloadTests`), l'esportazione del logo in
  `%LOCALAPPDATA%\AIUsageMonitor\notifications`, `ToastService` (sostituito da `NotificationService`), il pacchetto
  `Microsoft.Toolkit.Uwp.Notifications` e la sua voce in `THIRD-PARTY-NOTICES.md`.
- `TrayIconController.ShowNotification`, `AnnounceUpdate` e gli avvisi di `App.xaml.cs` ("AIUsageMonitor aggiornato",
  verifica icona) passano da `NotificationService`.
- Un avvio con gli argomenti di attivazione di un toast vecchio (`-ToastActivated`) viene ignorato senza errori; i toast
  vecchi nel Centro notifiche scadono da soli entro 24 h (`ExpirationTime`).
- `--test-notification` (ad app chiusa) mostra le card d'esempio — Finito, Permesso · Bash, Domanda, Errore e un avviso —
  e chiude l'app quando l'ultima e' stata chiusa o dopo 60 s. Serve alla verifica visiva.
- README (sezioni notifiche, `--test-notification`, impostazioni) aggiornato.

## 10. Componenti

| Unita' | Progetto | Responsabilita' |
|---|---|---|
| `AttentionDetail`, `AttentionKind` | Core/Notifications | Cosa aspetta una sessione in `NeedsInput` |
| `TranscriptAttentionReader`, `ToolSummary` | Core/Notifications | `tool_use` in sospeso → dettaglio; input dello strumento → testo breve |
| `NotificationComposer` | Core/Notifications | `SessionChange` + impostazioni → intento |
| `NotificationBoard` | Core/Notifications | Card visibili, coda, dedupe, ritiri, timer, silenzio |
| `NotificationPlacement` | Core/Notch | Geometria della pila |
| `SessionTracker`, `HookEvent`, `SessionState`, `HookEventPump` | Core | `idle_prompt`, `Silent`, `Attention`, `TurnStartedAt`, risoluzione live |
| `NotificationService` | App/Notifications | Collega sessioni, avvisi, board, finestra, suono e probe sul thread UI |
| `NotificationHostWindow`, `NotificationCardViewModel` | App/Notifications | Pila, card, animazioni, clic |
| `QuietModeProbe` | App/Notifications | P/Invoke: silenzio |
| `NotificationSound`, `Assets/Sounds/*.wav`, `tools/sounds/generate-sounds.js` | App, tools | I due suoni e la loro riproduzione |
| Impostazioni | Core/App | `NotifySound`, etichetta "Permessi e domande" |

## 11. Errori

- Nessun errore delle notifiche ferma il monitoraggio: lettura del transcript, probe, suono e finestra sono protetti
  e scrivono nel log solo il tipo di errore (mai nomi di sessione, messaggi o comandi).
- Transcript enorme: si leggono solo gli ultimi 256 KB; `tool_use` oltre quel limite → dettaglio generico.
- Monitor del notch scollegato: la pila segue lo stesso ripiego del notch (schermo primario).

## 12. Test

- `WaitingAndIdleTests`/`SessionTrackerTests`: `idle_prompt` dopo `Stop` non cambia nulla; su `Working` senza agenti
  porta a `Idle` con `Silent`; con agenti o workflow in corso e' ignorato; `Attention` copiato e azzerato;
  `TurnStartedAt` impostato a inizio turno e conservato dopo un permesso concesso; i test esistenti su `AwaitsPrompt` riscritti sul nuovo
  comportamento.
- `TranscriptAttentionReaderTests` con transcript di prova: ogni riga della tabella di §4.2, `tool_use` risolto, piu'
  `tool_use` in parallelo, messaggio su piu' righe, righe corrotte, file oltre 256 KB, file assente o bloccato.
- `NotificationComposerTests`: ogni tipo della tabella di §5.1, filtri per evento e per agente, `Silent`, prefisso di
  origine.
- `NotificationBoardTests`: sostituzione per sessione, ritiri, dedupe dopo ✕, massimo 3 e "+N", precedenza delle card
  persistenti, scadenze con orologio finto, pausa al passaggio del mouse, coda e uscita dal silenzio.
- `NotificationPlacementTests`: linguetta normale e compatta, pannello aperto, notch nascosto, offset, DPI 100/150 %,
  monitor secondario con origine negativa, pila piu' alta dell'area di lavoro.
- `HookEventPumpTests`: risoluzione solo per gli eventi live, mai nel replay.
- `ColorContrastTests`/`ThemePaletteTests`: nuovi abbinamenti.
- Suoni: il composer assegna `Done`/`Attention`/`None` per tipo e impostazione; il board restituisce il suono piu' forte,
  nessuno in silenzio, uno solo all'uscita; i due WAV esistono come risorse, hanno intestazione RIFF/PCM 16 bit mono
  44,1 kHz, durata 520 e 420 ms (±5 ms) e picco entro ±0,5 dB dal valore di spec; lo script rigenera file identici.
- Verifica manuale: `--test-notification`, un turno reale che finisce (una sola card, nessuna dopo 60 s), un permesso
  Bash reale (comando nella card, ritiro alla concessione), una domanda, schermo intero e "Non disturbare".

## 13. Fuori ambito

- Approvare un permesso dalla card: richiederebbe l'hook `PermissionRequest`, escluso dalla spec 2026-09-13 per non
  interferire con il flusso dei permessi.
- Cronologia delle notifiche e silenziamento per singola sessione.
- Dettaglio dei permessi di Codex (Codex non manda eventi `Notification`).
