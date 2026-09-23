# AIUsageMonitor

App Windows sempre attiva che mostra in un unico punto, per Claude Code e OpenAI Codex:

- la **quota consumata** nelle finestre di rate limit (percentuale e orario di reset);
- lo **stato live delle sessioni** aperte (al lavoro, attende input, finito, errore);
- il **costo API equivalente** in euro di ogni sessione, ai prezzi di listino di Anthropic e OpenAI.

Due superfici: un'icona nella **tray** con menu e pallino di stato, e un **notch laterale** sul bordo
destro dello schermo, sempre in primo piano, collassato a una linguetta da 28 px con una riga per
agente e che si espande al passaggio del mouse (clic sulla linguetta per fissarlo aperto).

Si aggiorna da sola dalle [release GitHub](#aggiornamenti) di questo repository, per chi ha accesso
alle release e collega il proprio account GitHub dalle impostazioni.

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

Le notifiche Windows usano il logo app esplicito e vengono testate con
`AIUsageMonitor.exe --test-notification` dopo aver chiuso l'app: il click apre il notch. Il PNG viene
generato in `%LOCALAPPDATA%\AIUsageMonitor\notifications\app-logo-<hash>.png` e conservato per
non invalidare i toast già inviati; le notifiche già presenti nel Centro notifiche non vengono
riscritte né azzerate.

## Requisiti

- Windows 10 versione 2004 (build 10.0.19041.0) o successivo, oppure Windows 11, x64.
- **.NET 10 Desktop Runtime** per la build framework-dependent (`AIUsageMonitor-<versione>-win-x64.exe`
  nelle release, quella prodotta di default da `scripts/publish.ps1`). La variante self-contained
  (`AIUsageMonitor-<versione>-win-x64-selfcontained.exe`, `-SelfContained`) non richiede nulla ma pesa
  ~180 MB.
- **Node.js** (già presente se usi Claude Code o Codex): serve solo per lo stato live, perché gli
  hook degli agenti eseguono uno script `.cjs`.
- **Git for Windows** con Git Credential Manager (è nell'installer standard): serve solo per
  collegare l'account GitHub dell'aggiornamento integrato.
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

**Aggiornamento a comando.** Il pulsante ⟳ nell'intestazione di ogni card aggiorna subito la quota
di quell'agente e ricalcola token e costi di tutte le sue sessioni, anche di quelle ferme; l'icona
gira finché non ha finito (al massimo 15 s) e il pulsante resta attenuato per 10 s prima di
accettare un nuovo click. "Aggiorna ora" nel menu della tray fa lo stesso per tutti gli agenti
attivi.

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

### Token e workflow attivi

Ogni sessione mostra separatamente **↑ input** (inclusa la cache) e **↓ output**.
Sono i token cumulativi della conversazione: il contesto inviato di nuovo a ogni richiesta viene
conteggiato di nuovo, quindi possono superare di molto la lunghezza del testo visibile. Il tooltip
separa input senza cache, cache letta e cache scritta. Le risposte Claude in streaming vengono
deduplicate per identificativo del messaggio, con ripiego sull'identificativo della richiesta;
per Codex si sommano le crescite del totale cumulativo riportato dal thread, contando per intero le
ripartenze da zero (Codex azzera il totale quando risveglia un thread per un nuovo compito) e
lasciando fuori la cronologia che un subagente forkato copia dal padre, già contata sul padre.
Così i token di ogni riga sono esattamente quelli di cui la riga mostra il costo.

La lista espandibile dei workflow mostra solo gli agenti **in corso**, con modello effettivo letto
dal transcript e token ↑ input/↓ output. Gli agenti conclusi escono dalla lista e dal riepilogo attivo.
Se modello o token non sono ancora disponibili viene mostrato "in attesa"; i totali della sessione
e quelli dei singoli workflow rimangono distinti.

### Codex: approvazione degli hook

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

## Costi

Accanto ai token di ogni sessione e di ogni agente di workflow il notch mostra il **costo API
equivalente**: quanto costerebbero quei token ai prezzi di listino pubblicati da Anthropic e OpenAI,
convertiti in euro. Con un piano in abbonamento (Max, Pro) **non è la spesa reale**. La card di ogni
agente riporta il totale delle sessioni presenti nel notch, compresi tutti i loro agenti; il tooltip
mostra il dettaglio per modello, la data del listino e il tasso usato.

- **Conteggio.** I token sono divisi per modello, per variante di prezzo (fast mode di Claude,
  priority e flex di OpenAI) e per fascia di contesto (prompt oltre 200k o 272k token), separando
  input, output, cache letta e cache scritta a 5 minuti o a 1 ora: Claude Code scrive solo cache a 1
  ora, che costa il 60% in più. Per Codex il costo segue il modello in vigore a ogni richiesta,
  anche quando cambia a metà thread. Le ricerche web di Claude si pagano a parte.
- **Listino.** Il listino [LiteLLM](https://github.com/BerriAI/litellm)
  (`model_prices_and_context_window.json`, ogni voce cita la pagina prezzi del vendor) viene
  scaricato al massimo una volta al giorno e salvato in `prices-cache.json`; fino al primo download
  vale la copia imbarcata nell'exe. Un file `prices-override.json` nella stessa cartella, con lo
  stesso formato per modello di LiteLLM, aggiunge o corregge prezzi:

  ```json
  { "codex-auto-review": { "input_cost_per_token": 1e-7, "output_cost_per_token": 5e-7 } }
  ```

- **Cambio.** Tasso di riferimento BCE (dollari per euro), scaricato una volta al giorno; senza rete
  vale l'ultimo scaricato, poi il tasso di riserva delle impostazioni (default 1 € = 1,14 $).
- **Modelli senza prezzo** (per esempio `codex-auto-review`, che nessun listino pubblica): il costo
  mostrato è un minimo, `≥ 1,20 €`, e il tooltip dice quale modello manca; se nessun modello ha un
  prezzo compare `costo n/d`.

Dalle impostazioni (gruppo COSTI) si nascondono i costi — e con loro ogni download di listino e
tasso — e si imposta il tasso di riserva.

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

**Herdr** è l'unica strategia che arriva al pane: porta in primo piano il pane esatto anche se si
trova in un'altra tab o in un altro workspace. **Senza Herdr** l'app attiva la finestra risolta
risalendo i processi quando l'evento è arrivato: è quella giusta anche con più finestre di Windows
Terminal aperte, perché non dipende da quante sono ma dall'antenato del processo che ha eseguito
l'hook. Quello che non può fare è scegliere la tab — l'attivazione agisce sulla finestra top-level —
quindi con più tab in una stessa finestra il click porta in primo piano la finestra giusta con la
tab che era attiva. In **VS Code**, se la risalita non arriva a una finestra, resta l'indizio
`VSCODE_PID` e l'app attiva la finestra di quel processo: le finestre di VS Code appartengono tutte
allo stesso processo, quindi con più finestre aperte può venire in primo piano una finestra diversa
da quella della sessione, senza alcun avviso. L'unico caso in cui l'app rinuncia davvero è il
ripiego su `WT_SESSION`: quando la risalita non è disponibile (sessione ricostruita dal replay senza
`host`, o pid già riciclato) e sono aperte più finestre di Windows Terminal, `WT_SESSION` non dice
quale sia e compare il toast "Terminale non trovato". Nessuna chiamata a `herdr` né risalita dei
processi blocca mai l'interfaccia: girano su thread separati con un timeout di 3 s, e il click non
ruba mai il fuoco allo schermo se non come conseguenza diretta del click stesso.

Per verificare senza passare dal notch, `AIUsageMonitor.exe --focus-session <sessionId>` aspetta il
replay iniziale della coda eventi e prova a portare in primo piano il terminale di quella sessione,
scrivendo l'esito nel log.

Chi ha già installato gli hook non deve reinstallarli per usare questa funzione: se risultano già
installati per almeno un agente, a ogni avvio l'app riallinea `hook.cjs` alla versione imbarcata
nell'eseguibile (lo riscrive solo quando il contenuto è cambiato). Se il file è bloccato o non
scrivibile l'app parte lo stesso: annota l'errore nel log, tiene la versione precedente dello script
e riprova al riavvio successivo.

## Aggiornamenti

Ogni push su `main` pubblica una release del repository (vedi [Rilasci](#rilasci)); l'app la trova,
la scarica e si sostituisce da sola. Serve un account GitHub che **veda le release del repository**:
senza accesso non c'è niente da controllare né da scaricare.

1. **Collegamento.** In *Impostazioni → AGGIORNAMENTI* il pulsante **Collega GitHub e controlla**
   avvia Git Credential Manager in modalità browser: GitHub chiede di scegliere l'account e, la prima
   volta, di autorizzare Git Credential Manager. Ogni collegamento usa un namespace GCM nuovo, quindi
   non riusa né modifica gli account salvati in Gestione credenziali di Windows. La sessione
   restituita viene cifrata con DPAPI (utente Windows corrente) in
   `%LOCALAPPDATA%\AIUsageMonitor\updates-auth.json`, ed è l'**unica** credenziale usata: mai PAT,
   account di GitHub Desktop o credential helper di git. **Scollega account** la elimina.
2. **Controllo.** Con un account collegato e *Controlla automaticamente gli aggiornamenti* attivo
   (default, vale dopo *Salva*) l'app controlla 15 secondi dopo l'avvio e poi ogni 6 ore; **Controlla
   ora** lo fa subito. Legge le 100 release più recenti e sceglie la versione SemVer stabile più alta
   (non l'etichetta "Latest"), purché contenga esattamente l'eseguibile della **stessa variante**
   in esecuzione (framework-dependent o self-contained, registrata nella build).
3. **Proposta.** Quando c'è una versione nuova compare una notifica Windows (una volta per versione)
   e in cima al menu della tray la voce **Aggiorna alla versione X…**. Entrambe aprono una conferma
   con un solo consenso per scaricare e riavviare; **Più tardi** ignora quella versione fino al
   riavvio dell'app. Dalle impostazioni gli stessi passi sono separati (**Scarica**, **Installa e
   riavvia**) e **Apri release** mostra la pagina GitHub con le note.
4. **Download verificato.** Prima di scaricare l'app rilegge la release scelta, per accorgersi di un
   asset sostituito nel frattempo. Gli indirizzi ammessi sono solo l'API del repository, i download
   delle sue release e le CDN degli asset di GitHub; i redirect vengono seguiti uno alla volta e
   ricontrollati, e il token va solo ad `api.github.com`. Lo SHA-256 del file scaricato deve
   coincidere con `SHA256SUMS.txt` della release e con il digest pubblicato da GitHub (se ci sono
   entrambi devono coincidere anche fra loro); il file deve essere un eseguibile Windows e la versione
   scritta nell'eseguibile deve essere quella della release.
5. **Installazione.** L'eseguibile è un file singolo senza installer: l'app lo verifica di nuovo,
   rinomina l'exe in esecuzione in `AIUsageMonitor.exe.old-<id>` (Windows permette di rinominare un
   exe in uso, non di sovrascriverlo), mette la nuova versione **nello stesso percorso** e la avvia con
   `--updated <pid>`, poi si chiude. La nuova istanza aspetta che la precedente sia uscita prima di
   prendere il mutex di istanza singola e mostra la notifica "AIUsageMonitor aggiornato". Avvio
   automatico, pin sulla barra e notifiche restano validi perché il percorso non cambia; impostazioni,
   hook e cache non vengono toccati. Se un passo fallisce l'exe precedente torna al suo posto. Se
   chiudi l'app (Esci, fine sessione) mentre la sostituzione è in corso, l'app aspetta che finisca e
   non si riapre: la nuova versione parte al prossimo avvio. I file `.old-*` vengono eliminati
   all'avvio successivo.

L'installazione integrata richiede una cartella dell'eseguibile scrivibile dall'utente: da una
cartella protetta (es. `Program Files`) o da `dotnet run` l'app segnala comunque la nuova versione,
ma va scaricata dalla pagina della release e sostituita a mano. La prova di scrittura (un file
temporaneo accanto all'exe) si fa solo quando c'è una versione da installare o quando scarichi o
installi, mai all'avvio: con *Accesso controllato alle cartelle* di Windows attivo e l'exe sul
Desktop o in Documenti, Windows Security può segnalarla in quei momenti. `AIUsageMonitor.exe --updates` apre
le impostazioni già sul gruppo AGGIORNAMENTI.

## Dove finiscono i file

| Percorso | Contenuto |
|---|---|
| `%LOCALAPPDATA%\AIUsageMonitor\settings.json` | impostazioni dell'app |
| `%LOCALAPPDATA%\AIUsageMonitor\usage-cache.json` | ultimo snapshot di quota per agente |
| `%LOCALAPPDATA%\AIUsageMonitor\prices-cache.json` | listino prezzi LiteLLM scaricato (solo modelli Anthropic e OpenAI) |
| `%LOCALAPPDATA%\AIUsageMonitor\prices-override.json` | prezzi aggiunti o corretti a mano (facoltativo) |
| `%LOCALAPPDATA%\AIUsageMonitor\exchange-rate.json` | ultimo tasso di riferimento BCE |
| `%LOCALAPPDATA%\AIUsageMonitor\logs\app-<data>.log` | log (7 giorni, livello Info) |
| `%LOCALAPPDATA%\AIUsageMonitor\updates-auth.json` | sessione GitHub dell'updater, cifrata con DPAPI |
| `%LOCALAPPDATA%\AIUsageMonitor\updates\` | nuova versione scaricata in attesa di installazione (i file obsoleti o più vecchi di 7 giorni vengono eliminati) |
| `<cartella dell'exe>\AIUsageMonitor.exe.old-<id>` | versione precedente dopo un aggiornamento, eliminata all'avvio successivo |
| `%USERPROFILE%\.aiusagemonitor\hook.cjs` | script hook installato |
| `%USERPROFILE%\.aiusagemonitor\events.jsonl` | eventi degli agenti (ruotato a 5 MB) |
| `%USERPROFILE%\.aiusagemonitor\backups\` | backup dei file di configurazione prima di ogni modifica |

Impostazioni disponibili: agenti attivi e intervallo di refresh, monitor e offset verticale del
notch, ritardo di chiusura, modalità compatta, quali notifiche mostrare e per quali agenti, controllo
automatico degli aggiornamenti, costi (mostra o nascondi, tasso di riserva), avvio automatico (valore
`AIUsageMonitor` in `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`).

## Build, test, publish

```powershell
dotnet build AIUsageMonitor.slnx
dotnet test AIUsageMonitor.slnx          # test del Core
node --test tests/hook/hook.test.cjs scripts/windows-release.test.mjs   # test dello script hook e dei rilasci
dotnet run --project src/AIUsageMonitor.App

pwsh scripts/publish.ps1                     # -> publish/AIUsageMonitor.exe (serve il .NET 10 Desktop Runtime)
pwsh scripts/publish.ps1 -SelfContained -Output publish-sc   # eseguibile autonomo (~180 MB)
pwsh scripts/publish.ps1 -Version 0.2.0      # stessa build con la versione indicata, come fa la CI

dotnet run --project tools/MakeIcon       # rigenera src/AIUsageMonitor.App/Assets/app.ico
dotnet run --project src/AIUsageMonitor.Probe -- --update-price-snapshot   # rigenera la copia del listino imbarcata
```

Per la build framework-dependent lo script usa `--no-self-contained`: con l'SDK .NET 10 e
`PublishSingleFile`, `--self-contained false` produce comunque un eseguibile self-contained.

L'app è a istanza singola (mutex `Local\AIUsageMonitor`): un secondo avvio fissa aperto il notch
dell'istanza già in esecuzione invece di aprirne un'altra.

### Rilasci

Le build locali non pubblicano nulla. `.github/workflows/windows-release.yml` gira a ogni push su
`main` (e a mano con *Run workflow*) su un runner Windows:

1. `node scripts/windows-release.mjs prepare` calcola la versione: parte dall'ultima release
   pubblicata (o dalla `<Version>` di `src/AIUsageMonitor.App/AIUsageMonitor.App.csproj` se è più
   alta) e la incrementa secondo i conventional commit arrivati da allora: `feat:` → minor, `!` o
   `BREAKING CHANGE:` → major, tutto il resto → patch. Un commit già contenuto in una release non ne
   crea un'altra.
2. Test .NET e Node, poi `dotnet publish -p:Version=<versione>` delle due varianti single-file
   win-x64 e uno smoke test che controlla header, versione scritta nell'eseguibile e variante.
3. `node scripts/windows-release.mjs publish` crea la release `v<versione>` in bozza con
   `AIUsageMonitor-<versione>-win-x64.exe`, `AIUsageMonitor-<versione>-win-x64-selfcontained.exe` e
   `SHA256SUMS.txt`, verifica gli upload e solo allora la pubblica come *Latest*.

La versione vive nei tag e nelle release: il workflow non fa commit né push sul repository.

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
eventi) e per la quota fa **una sola chiamata di rete**: l'endpoint usage di Anthropic, con il token
OAuth già presente sulla macchina. Con i costi attivi (default) fa anche due richieste anonime, al
massimo una volta al giorno e senza inviare alcun dato: il listino prezzi LiteLLM da
`raw.githubusercontent.com` e il tasso di riferimento da `www.ecb.europa.eu`. L'unica altra rete è
quella dell'updater, e solo dopo che hai collegato un account GitHub: l'API release di questo
repository e il download dei suoi asset, con la sessione creata dall'app (mai scritta nei log né
mostrata). Nessun prompt, nessun contenuto di conversazione e nessun token viene inviato, registrato
o mostrato da nessuna parte: gli eventi tracciati sono nomi di evento, identificativo di sessione,
cartella di lavoro, un messaggio breve dell'agente e, sugli eventi di avvio e di prompt, qualche
indizio sul terminale che ospita la sessione (ppid, pane di Herdr, `WT_SESSION`, `TERM_PROGRAM`,
`VSCODE_PID`), che serve solo al click "vai al terminale" e non lascia mai la macchina. Non c'è
telemetria.

## Licenza

MIT — vedi [LICENSE](LICENSE). Note su icone, listino prezzi e ispirazione in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
