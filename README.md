# AIUsageMonitor

App Windows sempre attiva che mostra in un unico punto, per Claude Code e OpenAI Codex:

- la **quota consumata** nelle finestre di rate limit (percentuale e orario di reset);
- lo **stato live delle sessioni** aperte (al lavoro, attende input, finito, errore).

Due superfici: un'icona nella **tray** con menu e pallino di stato, e un **notch laterale** sul bordo
destro dello schermo, sempre in primo piano, collassato a una linguetta da 28 px con una riga per
agente e che si espande al passaggio del mouse (clic sulla linguetta per fissarlo aperto).

Ispirata ad [AgentBar](https://github.com/scari/AgentBar) (macOS), riscritta da zero per Windows.

> _Screenshot: `docs/screenshot.png` (da aggiungere)._

## Aspetto

Tray, menu della tray e finestra **Impostazioni** condividono la stessa palette scura del notch
(sfondo `#1B1B1F`, superfici `#2A2A30` con hover `#3A3A42` e stato premuto `#45454E`, testo bianco
con sottotitoli in grigio chiaro, accento verde `#3FB950`), con contrasto testo/sfondo verificato
da test automatici (WCAG AA, ≥ 4.5:1). La finestra Impostazioni ha la barra del titolo scura (DWM
immersive dark mode) oltre ai controlli ristilizzati; l'apertura e la chiusura del notch usano una
dissolvenza incrociata tra linguetta e pannello, così non si sovrappongono più durante la
transizione.

Per controllare l'aspetto senza passare dal tray, l'eseguibile accetta due argomenti di debug:
`AIUsageMonitor.exe --settings` apre subito la finestra Impostazioni, `AIUsageMonitor.exe
--tray-menu` apre il menu della tray al centro dello schermo primario.

## Requisiti

- Windows 10/11 x64.
- **.NET 10 Desktop Runtime** per la build framework-dependent (quella prodotta di default da
  `scripts/publish.ps1`). La variante `-SelfContained` non richiede nulla ma pesa ~80 MB.
- **Node.js** (già presente se usi Claude Code o Codex): serve solo per lo stato live, perché gli
  hook degli agenti eseguono uno script `.cjs`.
- .NET SDK 10 per compilare dai sorgenti.

## Come legge la quota

**Claude Code.** Legge `%USERPROFILE%\.claude\.credentials.json` (campo `claudeAiOauth`) e chiama
`GET https://api.anthropic.com/api/oauth/usage` con quel token. Ne ricava la finestra 5h, la finestra
7g, le finestre settimanali per modello, l'eventuale extra usage e l'etichetta del piano. Il file di
credenziali è solo letto, mai scritto; il token non compare mai nei log né nell'interfaccia. Se il
token è scaduto o il file manca non viene fatta alcuna chiamata e la card mostra "Apri Claude Code
per rinnovare la sessione". Refresh ogni 60 s (configurabile 30–600 s).

**Codex.** Non c'è un endpoint di quota: i valori si leggono dai file di sessione
`%USERPROFILE%\.codex\sessions\YYYY\MM\DD\rollout-*.jsonl`, cercando dalla coda gli ultimi record
`token_count` con `rate_limits` (finestra primaria e secondaria, `used_percent`, `resets_at`,
`plan_type`). Vengono considerati i file modificati negli ultimi 7 giorni. Se non c'è nessuna
sessione recente la card mostra "Nessuna sessione Codex recente". Aggiornamento su modifica della
cartella (debounce 2 s) più un controllo periodico ogni 30 s.

L'ultimo snapshot valido è messo in cache in `%LOCALAPPDATA%\AIUsageMonitor\usage-cache.json`, così
all'avvio le barre sono già popolate (marcate "non aggiornate") finché non arriva il primo refresh.

## Come funziona lo stato live (hook)

Gli agenti non espongono il loro stato: lo ricaviamo dai loro hook.

1. Dal menu della tray (`Installa hook`) o dalle impostazioni, l'app copia
   `%USERPROFILE%\.aiusagemonitor\hook.cjs` e registra i propri gruppi di hook nei file di
   configurazione degli agenti: `~/.claude/settings.json` per Claude Code, `~/.codex/hooks.json`
   per Codex. Prima di ogni scrittura salva un backup in `~/.aiusagemonitor/backups/`, e riscrive il
   JSON preservando tutte le altre chiavi. Se un comando dell'app è già presente non viene duplicato.
2. A ogni evento l'agente esegue `node "<...>\hook.cjs" claude|codex`. Lo script legge stdin,
   appende **una riga JSON** a `%USERPROFILE%\.aiusagemonitor\events.jsonl` (timestamp, agente,
   evento, `session_id`, `cwd`, tipo di notifica, messaggio troncato a 200 caratteri) ed esce sempre
   con codice 0: non può bloccare né rallentare l'agente. Non vengono registrati i prompt.
3. L'app segue il file in append e ricostruisce la macchina a stati per ogni sessione:
   `SessionStart` → pronto, `UserPromptSubmit` → al lavoro, `Notification` di tipo
   `permission_prompt`/`idle_prompt`/… → attende input, `Stop` → finito, `StopFailure` → errore,
   `SessionEnd` → sessione rimossa. All'avvio rilegge le ultime 24 ore (in silenzio, senza toast).

### Codex: gli hook vanno approvati con `/hooks`

Codex esegue un gruppo di hook **solo dopo che l'utente lo ha approvato**: in `~/.codex/config.toml`
tiene una sezione `[hooks.state.'<file>:<evento>:<gruppo>:<indice>']` con un `trusted_hash` per ogni
gruppo approvato. Dopo `Installa hook` bisogna quindi aprire Codex e lanciare **`/hooks`** per
approvare i gruppi appena aggiunti; finché manca il `trusted_hash` l'app lo segnala nello stato con
"(da approvare in Codex con /hooks)" e il notch non mostra sessioni Codex.

I gruppi dell'app vengono sempre aggiunti **in coda**, così le chiavi posizionali dei gruppi già
approvati restano valide. Attenzione al contrario: rimuovendo il gruppo dell'app si spostano gli
indici dei gruppi aggiunti dopo, che vanno riapprovati con `/hooks`.

Se in `config.toml` gli hook sono disabilitati (`hooks = false`) l'app lo segnala senza modificare
il file.

## Vai al terminale

Nel notch, cliccando il nome di una sessione (il cursore diventa una manina) l'app porta in primo
piano il terminale che ospita quella sessione di Claude Code o Codex, senza scollegare il pin del
notch.

1. L'hook (`hook.cjs`) registra, solo sugli eventi `SessionStart` e `UserPromptSubmit`, un oggetto
   `host` con l'ambiente del processo che lo ha eseguito: `HERDR_PANE_ID`, `WT_SESSION`,
   `TERM_PROGRAM`, `VSCODE_PID` e il ppid. Le sessioni avviate prima di installare questa versione
   non hanno `host` finché non emettono il prompt successivo: fino ad allora il nome non è
   cliccabile.
2. Quando l'evento arriva, l'app risale l'albero dei processi a partire da quel ppid — il processo
   che ha eseguito l'hook vive pochi secondi, la finestra del terminale resta — e tiene per ogni
   sessione il pid dell'agente e il pid del primo antenato con una finestra top-level.
3. Al click la catena di strategie è: prima **Herdr** (`herdr agent focus <pane>`, con
   `herdr tab focus` come ripiego se il pane non risponde più), poi la finestra risolta al passo
   precedente, infine gli indizi residui (`VSCODE_PID`, `WT_SESSION`). Se nessuna strategia
   funziona compare il toast "Terminale non trovato".

**Herdr** è supportato in modo completo: porta in primo piano il pane esatto anche se si trova in
un'altra tab o in un altro workspace. **Windows Terminal, VS Code e le console semplici** sono
supportati "a comportamento migliore": funzionano bene con una sola finestra, ma con più finestre
di Windows Terminal (o più finestre VS Code) senza Herdr l'app non può sapere quale ospita la
sessione e rinuncia. Nessuna chiamata a `herdr` né risalita dei processi blocca mai l'interfaccia:
girano su thread separati con un timeout di 3 s, e il click non ruba mai il fuoco allo schermo se
non come conseguenza diretta del click stesso.

Per verificare senza passare dal notch, `AIUsageMonitor.exe --focus-session <sessionId>` aspetta il
replay iniziale della coda eventi e prova a portare in primo piano il terminale di quella sessione,
scrivendo l'esito nel log.

Chi ha già installato gli hook non deve reinstallarli per usare questa funzione: se risultano già
installati per almeno un agente, a ogni avvio l'app riallinea `hook.cjs` alla versione imbarcata
nell'eseguibile (lo riscrive solo quando il contenuto è cambiato).

## Dove finiscono i file

| Percorso | Contenuto |
|---|---|
| `%LOCALAPPDATA%\AIUsageMonitor\settings.json` | impostazioni dell'app |
| `%LOCALAPPDATA%\AIUsageMonitor\usage-cache.json` | ultimo snapshot di quota per agente |
| `%LOCALAPPDATA%\AIUsageMonitor\logs\app-<data>.log` | log (7 giorni, livello Info) |
| `%USERPROFILE%\.aiusagemonitor\hook.cjs` | script hook installato |
| `%USERPROFILE%\.aiusagemonitor\events.jsonl` | eventi degli agenti (ruotato a 5 MB) |
| `%USERPROFILE%\.aiusagemonitor\backups\` | backup dei file di configurazione prima di ogni modifica |

Impostazioni disponibili: agenti attivi e intervallo di refresh, monitor e offset verticale del
notch, ritardo di chiusura, modalità compatta, quali notifiche mostrare e per quali agenti, avvio
automatico (valore `AIUsageMonitor` in `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`).

## Build, test, publish

```powershell
dotnet build AIUsageMonitor.slnx
dotnet test AIUsageMonitor.slnx          # test del Core
node --test tests/hook/hook.test.cjs     # test dello script hook
dotnet run --project src/AIUsageMonitor.App

pwsh scripts/publish.ps1                     # -> publish/AIUsageMonitor.exe (serve il .NET 10 Desktop Runtime)
pwsh scripts/publish.ps1 -SelfContained -Output publish-sc   # eseguibile autonomo (~80 MB)

dotnet run --project tools/MakeIcon       # rigenera src/AIUsageMonitor.App/Assets/app.ico
```

L'app è a istanza singola (mutex `Local\AIUsageMonitor`): un secondo avvio fissa aperto il notch
dell'istanza già in esecuzione invece di aprirne un'altra.

## Limiti noti

- **Claude Code, permessi:** quando concedi un permesso l'agente non emette alcun hook, quindi la
  sessione resta "attende input" fino al `Stop` successivo, cioè fino a fine turno. Fa eccezione
  `AskUserQuestion`, dove il `PostToolUse` riporta subito la sessione ad "al lavoro".
- **Codex, attende input:** Codex espone `PermissionRequest`, ma nella v1 non lo registriamo per non
  interferire con il flusso di approvazione. Di conseguenza le sessioni Codex non mostrano mai
  "attende input": passano da "al lavoro" a "finito".
- Lo stato live dipende dagli hook: senza installazione (e, per Codex, senza approvazione con
  `/hooks`) le card mostrano "Stato live non attivo: installa hook".
- Le quote Codex si aggiornano solo quando una sessione Codex scrive un nuovo `token_count`.

## Privacy

L'app **legge soltanto file locali** (credenziali Claude, sessioni Codex, configurazioni hook,
eventi) e fa **una sola chiamata di rete**: l'endpoint usage di Anthropic, con il token OAuth già
presente sulla macchina. Nessun prompt, nessun contenuto di conversazione e nessun token viene
inviato, registrato o mostrato da nessuna parte: gli eventi tracciati sono solo nomi di evento,
identificativo di sessione, cartella di lavoro e un messaggio breve dell'agente. Non c'è telemetria.

## Licenza

MIT — vedi [LICENSE](LICENSE). Note su icone e ispirazione in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
