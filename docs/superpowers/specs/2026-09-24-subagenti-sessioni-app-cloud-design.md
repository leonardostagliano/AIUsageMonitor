# Subagenti precisi, sessioni chiuse col terminale, sessioni dell'app e del cloud — Design

Data: 2026-09-24
Estende: [2026-09-13-aiusagemonitor-design.md](2026-09-13-aiusagemonitor-design.md) (stato live delle sessioni)

## 1. Problemi

1. **Subagenti imprecisi.** Il notch contava gli agenti da `SubagentStart`/`SubagentStop`. Un agente interrotto o
   ucciso non manda `SubagentStop` e restava "al lavoro" fino al timeout di 30 minuti; un agente lungo senza altri
   eventi veniva invece chiuso a 30 minuti mentre lavorava ancora; tra due fasi di un workflow in background la
   sessione andava a "finito" (con il toast) e tornava subito "al lavoro".
2. **Sessioni appese.** Chiudere il terminale uccide l'agente prima di `SessionEnd`: la sessione restava fino allo
   sweep di 12 ore, e a ogni riavvio dell'app il replay degli ultimi 24 h la riportava.
3. **Sessioni fuori dal terminale.** Le sessioni avviate dall'app desktop di Claude (SDK) possono non caricare gli
   hook dell'utente, e quelle nel cloud (claude.ai/code, app, routine) non girano sulla macchina.

## 2. Fonti verificate (Claude Code 2.1.282)

- **`background_tasks` negli hook.** `Stop` e `SubagentStop` portano l'elenco dei lavori in background ancora in
  corso: `{id, type, status, description, agent_type?, name?}`. Per un agente (`type: "subagent"`) l'`id` e' l'id
  dell'agente, lo stesso di `SubagentStart`. L'elenco contiene solo il lavoro *in background*: gli agenti in primo
  piano che girano in parallelo non ci sono, quindi e' affidabile solo sul `Stop` della sessione principale (a fine
  turno nessun agente in primo piano puo' essere ancora vivo).
- **Registro delle sessioni.** Ogni processo Claude Code (CLI, `-p`, SDK) scrive `~/.claude/sessions/<pid>.json` con
  `sessionId`, `cwd`, `startedAt`, `kind`, `entrypoint` (`cli`, `claude-desktop`, `sdk-ts`...), `status`
  (`busy`/`shell`/`idle`/`waiting`) e `waitingFor`; lo aggiorna a ogni cambio di stato e lo cancella all'uscita
  pulita. Un processo ucciso lascia il record.
- **API cloud.** `GET https://api.anthropic.com/v1/code/sessions` (la lista di `--teleport`) con il token OAuth e
  `anthropic-version: 2023-06-01`: `id`, `title`, `status`, `worker_status` (`running`/`idle`/`requires_action`),
  `last_event_at`, `environment_kind`, `config.model`, `external_metadata` (`pending_action`, `usage`).
  `GET /v1/code/triggers` con `anthropic-beta: ccr-triggers-2026-01-30` e `x-organization-uuid`: le routine con
  `last_run {status, fired_at, finished_at, session_id}`. Le esecuzioni delle routine non compaiono nella lista
  delle sessioni.

## 3. Soluzione

### 3.1 Subagenti (`SessionTracker`)

- `hook.cjs` inoltra `background_tasks` di `Stop`/`SubagentStop`, ridotto ad agenti e workflow; oltre 100 voci la
  lista diventa `null` (sconosciuta), mai tagliata.
- Sul `Stop` con lista: un agente in corso assente dalla lista diventa finito; uno presente e mai visto partire
  viene aggiunto; uno gia' finito non torna in corso (gara con `SubagentStop`). Il task `main-session` e i figli
  Codex sintetizzati sono esclusi.
- `PendingWorkflows`: i workflow in corso secondo l'ultimo `Stop`/`SubagentStop`. Con workflow in corso l'ultimo
  `SubagentStop` non porta la sessione a Idle; la chiude il `Stop` del turno che il workflow sveglia.
- Timeout: un agente senza eventi da 30 minuti resta in corso se il suo transcript e' stato scritto nel frattempo
  (`ITokenSource.SubagentLastActivity`); lo sweep rilascia anche una sessione ferma solo su un workflow silenzioso.

### 3.2 Processi (`SessionProcessRegistry`)

- Legame sessione → processo `(pid, ora di creazione, fonte)`: dal registro di Claude Code (fonte principale) e
  dalla risalita dei processi dell'hook (solo per eventi freschi). Un legame dalla risalita non sostituisce uno
  vivo del registro.
- Ogni 10 s la pump interroga il sistema (`OpenProcess` + `GetProcessTimes`): processo morto, o pid vivo ma creato
  in un altro momento (riciclato), per due giri di fila → `SessionEnd` sintetico. Un processo non interrogabile e'
  "sconosciuto" e non chiude nulla.
- `session-processes.json` conserva legami e sessioni finite (48 h, piu' della finestra di replay): una sessione
  chiusa non torna col replay; un evento successivo (ripresa) la riapre.
- All'avvio chiude anche le sessioni del replay nominate solo da record del registro lasciati da processi morti.

### 3.3 Sessioni dell'app (`ClaudeRegistrySessionFeed`)

- Ogni 3 s legge il registro. Un record vivo la cui sessione nessun hook ha riportato viene adottato dopo 8 s
  (tempo per il `SessionStart` di una CLI con gli hook) e ne segue lo stato: `busy`/`shell` → al lavoro,
  `waiting` → attende input, ritorno a `idle` → finito; record sparito o processo morto → fine.
- Origine `App` per `claude-desktop`, `local-agent` e SDK, `Terminal` per la CLI. L'host e' il pid del record: il
  click porta in primo piano la finestra dell'app (o del terminale). Il transcript si cerca in
  `~/.claude/projects` per token e costi.

### 3.4 Sessioni cloud (`ClaudeCloudSessionsClient`, `CloudSessionFeed`, `CloudSessionPoller`)

- Lettura alla cadenza della quota di Claude (minimo 30 s), opzione *Sessioni cloud e routine* (attiva di
  default). Token rifiutato o endpoint assente: nuovo tentativo dopo 10 minuti.
- Visibili mentre lavorano o attendono (al piu' 24 h senza notizie) e per 10 minuti dopo la fine; archiviate,
  sparite dalla lista o troppo vecchie → fine. Le sessioni Remote Control (`bridge`) sono locali e si saltano.
- Gli eventi passano dalla pump (`Inject`), la prima lettura in silenzio. Token e costo dall'`usage` dichiarato
  dalla sessione. Click → `https://claude.ai/code/session_<id>`.
- Click con l'app desktop di Claude gia' in esecuzione (un `claude.exe` in `%LOCALAPPDATA%\AnthropicClaude\[app-*\]`
  o in `<unita'>:\[Program Files\]WindowsApps\Claude_*\app\`, non la CLI, con finestre Electron anche nascoste in
  questa sessione) → `claude://claude.ai/code/session_<id>` (solo con id `[A-Za-z0-9_-]{1,128}`). Conta come aperta
  nell'app solo se entro 3 s una finestra dell'app viene in primo piano, compare o cambia titolo (se non viene in primo
  piano da se' la si attiva); altrimenti, o con il link rifiutato, si apre il browser come prima. Il percorso del link
  non e' documentato per le sessioni di Code: l'app potrebbe mostrare la home, e da fuori non si vede. L'app non viene
  mai avviata per un link.

### 3.5 Revisione dopo v0.4.0

- **Icona gialla con un agente al lavoro.** Claude Code manda `idle_prompt` quando la sessione principale resta ferma,
  anche con agenti in background al lavoro. `idle_prompt` e' ignorato finche' la sessione ha agenti o workflow in
  corso; un'attesa da `idle_prompt` (`AwaitsPrompt`) viene dopo "al lavoro" nello stato aggregato (tray, pallino
  della card, pillola) e un `SubagentStart` la chiude come chiude Idle. Un permesso concesso non manda hook: se il
  record del registro torna `busy` dopo la notifica (`WaitingSince`), la sessione torna al lavoro.
- **Sessioni finite che restano.** Le sessioni cloud e le routine finite restano 10 minuti (erano 6 ore). L'app
  desktop tiene aperto il processo di una conversazione finita: una sessione `app` Idle o in errore esce 10 minuti
  dopo l'ultimo evento, un record `idle` piu' vecchio non viene adottato, e i processi `spare` non ancora assegnati
  si ignorano.

## 4. Interfaccia

- Sottotitolo della riga con l'origine quando non e' un terminale: `cloud · al lavoro · 3m`, `app · finito · 1m`,
  `routine · errore · 2h`; stesso prefisso nel titolo dei toast.
- Impostazioni → Agenti → Claude Code: casella *Sessioni cloud e routine*.

## 5. Limiti

- Le API cloud sono quelle interne usate dalla CLI: se cambiano restano un messaggio nel log.
- Le sessioni Cowork (VM locale) non sono visibili; le sessioni cloud e quelle dell'app senza hook non hanno
  l'elenco degli agenti.
