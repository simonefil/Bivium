# XTerm.NET 2.0.2: stato dell'integrazione

Implementazione parziale del piano `2026-09-09 14_27 - plan - XTERM_NET_POST_UPGRADE.md`.

## Capability e paste

La configurazione della sessione mantiene `Cols = 120`, `Rows = 30`, `Scrollback = Math.Max(256, HeadlessScrollbackRows)`, `TermName = xterm-256color` e `ConvertEol = true`.

| Opzione | Default del fork 2.0.2 | Bivium |
| --- | --- | --- |
| SixelEnabled | true | false |
| KittyGraphicsEnabled | true | false |
| ITerm2ImagesEnabled | true | false |
| KittyKeyboardEnabled | true | true, usato solo dopo negoziazione |
| KittyNotificationsEnabled | true | false |
| ClipboardWriteEnabled | true | false |
| ClipboardReadEnabled | false | false |
| PointerShapesEnabled | true | false |
| AllowPasteControls | false | false |

`SendPaste` usa `Terminal.Paste` sotto il lock della sessione. L'evento `DataReceived` inoltra il risultato alla PTY. Il paste normalizza LF/CRLF in CR, conserva tab e Unicode, elimina gli altri controlli C0/C1 e DEL e applica il bracketed paste negoziato. Il normale input conserva il proprio percorso.

Le query DA, DECRQM, DECRQSS, XTGETTCAP e XTVERSION hanno test di risposta sul percorso `DataReceived`. I test verificano inoltre che le query Kitty graphics, notification e pointer shape non annuncino supporto; Kitty keyboard risponde alle query e conserva lo stack negoziato.

## Recovery del circuito

`connection.js` coordina il modal Blazor, la presenza del workspace e i renderer. Durante il distacco sospende le chiamate browser e invalida le richieste terminali pendenti. Il recupero esegue prima l'heartbeat del workspace e poi visibility, resize, handoff con pagina finale dello storico e focus. L'input riparte soltanto dopo il completamento. I tentativi concorrenti condividono una Promise; una nuova disconnessione invalida le continuazioni precedenti.

Il componente terminale rinnova la sottoscrizione runtime quando cambia la generazione della lease. Il coordinamento riusa le regole server esistenti di riacquisizione e takeover.

Un handoff fallito o superiore a 10 secondi provoca un reload della pagina. Il modal mantiene il fallback reconnect/resume/reload e ritenta ogni 5 secondi dopo lo stato `failed`. Non vengono modificati timeout o keepalive del server/proxy e non viene riavviata la PTY dal recovery UI.

Gli eventi e il significato di `hide`, `failed` e `rejected` seguono la [documentazione ASP.NET Core 10](https://learn.microsoft.com/en-us/aspnet/core/blazor/fundamentals/signalr?view=aspnetcore-10.0).

Verificato con harness JavaScript deterministico: blocco delle chiamate durante il distacco, ordine del recovery, renderer e listener non duplicati, interruzione durante il recupero, errori sincroni, timeout e reload su circuito o riferimenti inutilizzabili.

Verificato inoltre in Chrome headless reale su macOS, contro Bivium locale e il proxy di test: chiusura WebSocket 1001 osservata dal browser, nuovo WebSocket, stessa pagina e stesso PID della PTY, input/output funzionanti dopo il recovery, un solo renderer e nessuna Promise non gestita. La prima prova del driver confondeva focus e vecchi marker di output; il test salvato usa marker univoci, una PTY dedicata ed eventi DOM indirizzati esplicitamente al terminale.

Verificato anche il circuito non più recuperabile: il proxy ha impedito le riconnessioni per 205 secondi, oltre la retention del circuito. Al ripristino della rete Bivium ha ricaricato automaticamente la pagina; l'identificatore della pagina è cambiato, il PID della PTY è rimasto identico e input/output sono ripartiti. Nessuna Promise non gestita e un solo renderer. Il test del fallback riapre la finestra terminale dalla nuova pagina per verificare l'input, senza creare un'altra PTY.

Restano da verificare pagina nascosta, reverse proxy di produzione e seconda piattaforma. Recovery e fallback locali sono verificati; la causa originale della chiusura in produzione resta da diagnosticare.

## Shift+Invio e tastiera negoziata

Il browser inoltra `key`, `code`, Shift/Ctrl/Alt/Meta e gli eventi press/repeat/release tramite `OnTerminalKey`. Il runtime consulta `KittyKeyboardActive` e usa `GenerateKittyKeyInput` soltanto quando l'applicazione ha negoziato la modalità. Un risultato null significa evento soppresso e non attiva un secondo tentativo legacy.

Con `CSI > 1 u` attivo, Shift+Invio produce `CSI 13 ; 2 u`, mentre Invio semplice conserva CR. Con i flag 11 sono distinguibili anche repeat e release. Il pop dello stack ripristina la modalità precedente; senza negoziazione Shift+Invio conserva il comportamento legacy CR. Non vengono riconosciuti nomi di processi e non viene forzato un fallback specifico per Codex o Claude. Il contratto segue la [specifica Kitty keyboard](https://sw.kovidgoyal.net/kitty/keyboard-protocol/) e le API pubbliche del fork.

Il browser mantiene le scorciatoie clipboard esistenti e inoltra i tasti aggiuntivi quando lo snapshot indica Kitty attivo. Le pressioni gestite sono seguite dai rispettivi rilasci; il blur rilascia i tasti mantenuti. La sospensione cancella lo stato delle pressioni e non accoda tasti da riprodurre al reconnect. I release legacy non producono input e non riavviano una sessione terminata.

Il testo composto rimane sul percorso `OnTerminalInput`: il test end-to-end conserva esattamente `漢é👩‍💻🇮🇹`, senza doppio commit. Non viene sostituito il generatore della libreria per introdurre nuove codifiche Unicode.

Verifiche automatiche: negoziazione/query, push/pop, normal/alternate, Shift+Invio e Invio, Meta, codice fisico del tastierino, F13, testo BMP, repeat/release e soppressione senza fallback. Verifica browser → Blazor → PTY riuscita con `terminal-keyboard-probe.py`, che controlla i byte effettivi e ripristina il terminale al termine.

Accettazione eseguita il 9 settembre 2026 con Codex CLI 0.153.4 e Claude Code 2.1.265 reali, in Chrome headless su macOS, attraverso browser → Blazor → PTY. Le CLI sono state avviate in una cartella temporanea dedicata, con i profili locali e l'autorizzazione dell'utente. L'unico prompt inviato a ciascuna è stato `ciao come stai`.

Codex ha negoziato Kitty con `CSI > 7 u` e ricevuto la risposta `CSI ? 7 u`; Claude ha attivato il buffer alternate con `CSI ? 1049 h` e Kitty con `CSI > 5 u` (richiedendo anche modifyOtherKeys). In entrambe le CLI Shift+Invio ha prodotto esattamente `CSI 13 ; 2 u` e aggiunto una riga nel composer senza inviare il messaggio. Invio semplice ha prodotto CR e inviato il prompt. Una PTY intermedia trasparente ha registrato le sequenze di protocollo e i byte di input, senza simulare le risposte del terminale. L'accettazione locale di Shift+Invio nelle due CLI è conclusa; SSH e multiplexer restano da verificare separatamente.

## Synchronized output — fase 5

Bivium sottoscrive `SynchronizedOutputChanged` e mantiene l'ultimo snapshot completo mentre DEC 2026 è attivo. Il frame conserva insieme celle, cursore, revisione e confine dello storico: snapshot e patch concorrenti non anticipano gli stati intermedi; il paging resta limitato alle righe del frame visibile. Emulatore, risposte alla PTY, archiviazione, budget ed export continuano a elaborare lo stato corrente.

Il worker di notifica già esistente attende la fine del blocco e pubblica l'aggiornamento con la normale finestra di aggregazione di 16 ms. Il timeout confermato dall'utente è di 1 secondo, misurato con orologio monotono: scaduto il termine, gli aggiornamenti ripartono anche se non arriva altro output. Il blocco interrotto resta ignorato fino a una successiva transizione off/on, senza modificare lo stato negoziato in XTerm.NET.

Resize (anche a geometria invariata), handoff/reconnect e uscita della PTY interrompono l'attesa. L'handoff restituisce subito lo stato corrente; le notifiche usano il worker esistente. Il detach lascia attivi elaborazione e archivio; la rimozione della sessione elimina il frame conservato e termina il worker. Se il trimming elimina righe referenziate dal frame, Bivium riprende la pubblicazione per non mantenere indici obsoleti.

I 13 casi automatici coprono normal/alternate, output in più chunk, timeout senza ulteriori byte, ripresa dopo timeout, nuova negoziazione, resize, handoff, uscita, detach, dispose, paging, budget, blocchi consecutivi e reset. Il confronto finale verifica testo e cursore rispetto alla baseline.

Misure singole locali su un repaint sintetico di 12 righe, una scrittura ogni 30 ms:

| Misura | Senza DEC 2026 | Con DEC 2026 |
| --- | ---: | ---: |
| Snapshot/notifiche nel test runtime | 12 | 1 |
| Byte JSON dei payload snapshot nel test runtime | 842.291 | 126.958 |
| Repaint DOM osservati in Chrome | 12 | 1 |
| Tempo task del renderer Chrome | 46,56 ms | 6,59 ms |
| Tempo script del renderer Chrome | 13,46 ms | 1,85 ms |

I byte sono quelli degli snapshot serializzati nel test, non una misura del traffico WebSocket complessivo. I tempi Chrome sono metriche CDP della singola finestra di osservazione, non percentili né una misura della CPU totale server. Le due prove hanno lo stesso contenuto finale. Il driver esclude il frame iniziale `READY`, che può essere ripubblicato dal focus senza costituire un repaint intermedio.

Verificato anche btop reale su macOS: alternate buffer, sequenze `CSI ? 2026 h/l`, pannelli attivi dopo resize e ritorno alla shell all'uscita. Il confronto prestazionale controllato resta quello sintetico; non viene attribuita a btop una riduzione percentuale misurata.

Per ripetere il confronto browser, avviare Bivium e Chrome isolati come nella sezione di accettazione, quindi eseguire `node Bivium.Tests/terminal-synchronized-output-acceptance.mjs`. Il driver usa direttamente la porta 5186 (configurabile con `BIVIUM_TEST_PORT`), CDP 9226 e la fixture Python locale; non richiede il proxy o CLI di modelli. La prova è configurata per la shell zsh locale macOS e attende il suo prompt prima di inviare il comando.

## Palette dinamica e sottolineature — fase 6

Ogni frame completo espone la vista coerente di `Terminal.Colors.Take()`: palette a 256 colori, foreground e background predefiniti e colore del cursore. Il renderer usa questi valori soltanto nell'area terminale; barra del titolo, tab e resto di Bivium mantengono il tema dell'interfaccia. Le righe dello storico conservano gli indici e vengono quindi rappresentate con la palette corrente della sessione, come il buffer del terminale.

Per non ripetere i 256 colori in ogni aggiornamento, handoff e snapshot completi includono sempre la palette, mentre una patch ordinaria conserva quella già applicata dal browser. Una revisione che contiene OSC 4, 10, 11 o 12 include il nuovo stato; i reset OSC 104 e 110–112 seguono lo stesso percorso. Il protocollo resta coerente con synchronized output: una palette cambiata dentro DEC 2026 diventa visibile insieme al frame concluso.

Le celle distinguono underline single, double, curly, dotted e dashed. Il colore può seguire il foreground oppure essere indicizzato o RGB; nel browser curly usa lo stile CSS `wavy`. La vecchia flag underline resta nel contratto per le decorazioni combinate con strikethrough e overline. Il confronto dei checkpoint del reflow include anche i nuovi attributi, evitando di considerare equivalenti celle con sottolineature diverse.

Verificati modifiche successive e reset della palette, colori speciali, i cinque stili underline e colori indexed/RGB/default. In Chrome reale su macOS sono stati osservati area terminale `#abcdef`/`#102030`, cursore `#fedcba`, testo indicizzato `#123456` e underline curly `#445566`; dopo il reset sono tornati i valori xterm iniziali. Nessuna modifica al sorgente XTerm.NET.

## Navigazione dei prompt OSC 133 — fase 7

Bivium usa i marker `PromptStart` OSC 133 nativi di XTerm.NET e conserva la relativa colonna negli snapshot delle righe. Il metadato segue le celle durante reflow, passaggio dal viewport all'archivio e paging; non viene ricavato dal testo del prompt. Gli eventi `ShellIntegrationMarkReceived` e le API native `TryFindPreviousPrompt`/`TryFindNextPrompt` sono coperti da un test dedicato.

Nel buffer normale, `Ctrl+Freccia su` cerca il prompt precedente e `Ctrl+Freccia giù` quello successivo. Il server attraversa un'unica timeline composta da archivio esterno e schermata corrente; il browser porta la riga trovata nel viewport usando il paging già esistente. Pressioni successive continuano dalla posizione raggiunta. Input ordinario, paste, scroll manuale e ritorno al fondo aggiornano l'ancora di navigazione.

La scorciatoia viene intercettata soltanto dopo che la sessione ha realmente emesso almeno un marker OSC 133 e solo nel buffer normale. Senza shell integration, oppure dentro un'applicazione che usa il buffer alternate, `Ctrl+Freccia su/giù` conserva il percorso di input esistente verso la PTY. I marker restano invisibili: non sono stati aggiunti indicatori grafici e l'export testuale contiene solo input/output del terminale, senza sequenze OSC 133 o righe duplicate dovute a resize.

La prova Chrome reale su macOS ha generato tre prompt marcati separati da output sufficiente ad attraversare archivio e schermata corrente. Dal fondo, due `Ctrl+Freccia su` hanno raggiunto nell'ordine `PROMPT-3` e `PROMPT-2`; `Ctrl+Freccia giù` è tornato a `PROMPT-3`. La shell o l'applicazione deve configurare ed emettere OSC 133: Bivium non modifica automaticamente i profili di zsh, bash o PowerShell.

## Clipboard, notifiche, progresso e attenzione — fase 8

Le decisioni UX e di sicurezza sono applicate separatamente per ogni capability. Le richieste OSC 52 e Kitty OSC 5522 possono scrivere esclusivamente `text/plain` UTF-8, fino a 1 MiB. Bivium mostra un'anteprima e richiede il gesto esplicito `Copy`; `Cancel` scarta la richiesta. Le richieste sono accodate con un limite di otto elementi e l'anteprima visuale è limitata, mentre la conferma copia il testo completo. Clipboard read resta disabilitata e i formati binari vengono ignorati.

Le notifiche OSC 9 e Kitty OSC 99 producono toast testuali interni a Bivium. Al massimo cinque toast restano visibili contemporaneamente, scompaiono dopo cinque secondi e un clic apre il terminale e seleziona la sessione che li ha generati. Non vengono richieste notifiche del sistema operativo, suoni, icone o accesso aggiuntivo al browser.

Il progresso OSC 9;4 appartiene alla singola sessione: lo stato indeterminato mostra un'icona rotante nel relativo tab, gli stati determinati mostrano la percentuale e warning/error usano uno stile distinto. L'indicatore viene rimosso quando l'applicazione invia lo stato `None` o il processo termina. La command bar e il pulsante F12 non mostrano progresso.

Una richiesta iTerm2 `RequestAttention` fa lampeggiare tre volte il tab interessato e `F12 Terminal`, poi mantiene l'evidenziazione fino all'apertura della sessione. Se la sessione è già aperta e attiva, la richiesta viene riconosciuta senza lampeggio. Pointer shape resta disabilitata.

La prova Chrome reale ha verificato spinner e `42%` nel solo tab, toast e apertura della sessione, attenzione su tab/F12 con azzeramento, assenza di lampeggio sulla sessione già attiva e anteprima clipboard. Il clic `Copy` ha passato esattamente `copy me ✓` a una Clipboard API sostitutiva confinata alla pagina di test; la prova non ha letto né modificato la clipboard del sistema.

## Protocolli grafici — fase 10

Il supporto a immagini inline non rientra nello scope di Bivium per decisione esplicita. Sixel, Kitty Graphics e immagini iTerm2 restano disabilitati; Bivium non annuncia queste capability, non decodifica payload grafici invisibili e non introduce trasferimento, cache o placement di immagini nel browser. La fase è chiusa senza modifiche applicative.

## Ottimizzazioni misurate — fase 11

Il profiling è stato eseguito in Release usando i pattern locali basati su `Stopwatch`, `GC.GetAllocatedBytesForCurrentThread`, payload JSON reali e il progetto benchmark già presente nel fork. Le ottimizzazioni sono state mantenute solo quando la misura ha mostrato un beneficio materiale.

La serializzazione OSC 8 non chiama più `TryGetLinkAt` per ogni cella, operazione che nel fork scorre linearmente tutti i link della riga. Poiché `BufferLine.Links` garantisce intervalli ordinati da sinistra a destra, Bivium avanza una sola volta nella lista mentre visita le celle. Su 200 snapshot di una griglia 30×119 con 20 link per riga, la mediana di tre esecuzioni è passata da 92,9 ms a 76,1 ms, circa il 18% in meno; allocazioni e payload sono invariati.

Il percorso PTY passa ora i byte letti da `ShellService` direttamente a `Terminal.Write(ReadOnlySpan<byte>)`. Sono stati eliminati dal percorso di ricezione il decoder UTF-8, il buffer di caratteri e la stringa UTF-16 intermedi. Nel benchmark del fork da un secondo per corpus, il percorso byte è risultato 1,22–1,30 volte più veloce sui corpus normali, equivalente sul flood e 0,97 volte su alternate redraw; elimina le allocazioni di transcodifica ASCII, mentre Unicode passa da 5,17 a 4,10 byte allocati per carattere. Il test di regressione spezza deliberatamente una sequenza UTF-8 nel mezzo di `👩‍💻` e verifica nello stesso flusso anche OSC 8. Una prova Chrome reale attraverso Blazor e una PTY zsh ha restituito esattamente `BYTE-👩‍💻-✓`.

La misura finale distingue costruzione dello snapshot e serializzazione JSON:

| Scenario | Snapshot, 200 iterazioni | Allocazioni snapshot | JSON, 20 iterazioni | Allocazioni JSON | Payload |
| --- | ---: | ---: | ---: | ---: | ---: |
| 30×119 senza link | 63,6 ms | 67,02 MiB | 33,4 ms | 14,87 MiB | 777.071 byte |
| 30×119, 20 link per riga | 76,0 ms | 58,55 MiB | 29,4 ms | 13,39 MiB | 699.071 byte |

Nel test di synchronized output, lo stesso workload produce 12 snapshot, 1.418.939 byte e 17,961 ms senza DEC 2026, contro uno snapshot, 213.292 byte e 0,370 ms con DEC 2026. La serializzazione JSON di un frame completo resta un costo visibile, ma synchronized output elimina già i frame intermedi. Un nuovo protocollo compatto richiederebbe una modifica ampia del contratto frontend/backend e non è giustificato dalle misure attuali.

È stata provata anche la preallocazione dell'array delle celle e della capacità di `StringBuilder`: riduceva le allocazioni del 12–16%, ma peggiorava materialmente la latenza, quindi è stata rimossa. Nessun sorgente XTerm.NET è stato modificato.

## Reflow integrato nel runtime

Il gate `XTermPostUpgradeTests.ReflowPreservesCompletedArchiveAndLiveText` ora passa. La correzione è interamente in Bivium: nessuna modifica al sorgente XTerm.NET, nessuna cancellazione dello scrollback interno e nessuna registrazione grezza della PTY.

`TerminalHistoryReflow` applica al runtime la proiezione verificata nei 24 test di `ReflowProvenanceSpikeTests`:

1. Prima del resize congela gli snapshot delle righe visibili e la provenienza delle celle.
2. Per i cambi di larghezza del buffer normale costruisce una copia temporanea equivalente. Riga e colonna originarie sono marcate nei colori della sola copia.
3. Il `Resize` pubblico della copia trasferisce i marcatori con le celle, senza replicare l'algoritmo privato della libreria e senza cercare testo uguale.
4. Dopo il resize reale consolida i frammenti che non sono più visibili e trasferisce la provenienza sulle righe risultanti.
5. Se il clipping elimina un suffisso, consolida anche il prefisso ancora visibile: l'archivio resta ordinato e append-only. L'export omette le celle già consolidate.

Per i soli cambi di altezza e per il buffer alternate non serve la copia: vengono conservate le identità delle righe. Entrambi i buffer vengono seguiti anche mentre uno è inattivo.

Lo schermo inviato al renderer mantiene le righe e le coordinate reali di XTerm. Le celle archiviate possono quindi tornare visibili ingrandendo il terminale, ma non vengono salvate una seconda volta nell'archivio o nell'export. Una riga ricomposta può contenere insieme celle archiviate e celle ancora mutabili.

`LineExitedViewport` consolida solo i frammenti non ancora archiviati, sia nello scroll normale/alternate sia nella disattivazione del buffer. La cache pubblica della riga conserva i bit di provenienza e viene invalidata da scritture e riciclo. Un repaint rende mutabile lo stato corrente, senza causare da solo un append. Il confronto del checkpoint normale già esistente rimane limitato alla stessa riga e allo stesso stato; non elimina occorrenze uguali di output su righe diverse.

I budget e gli indici dell'archivio segmentato rimangono autorevoli. Il resize applica anche il budget globale. Le celle già scartate dal budget, ma ancora presenti nel ring nativo, non vengono reinserite nello storico.

### Hyperlink e metadati

La serializzazione legge gli intervalli OSC 8 nativi ordinati da `BufferLine.Links` con una scansione lineare per riga; l'indice parallelo e il callback `HyperlinkChanged` di Bivium sono stati rimossi. I test coprono link chiusi e aperti, wrapping/reflow, overwrite ed erase. Per ICH/DCH il fork attuale cancella i link nella regione spostata: Bivium rispecchia questo comportamento, senza attribuire URL obsoleti alle celle.

I gruppi OSC 66 vengono esclusi dal reflow della copia come nel buffer reale; le celle cancellate dal clipping non vengono considerate superstiti. Sono coperti due casi, incluso il taglio di un run. Questo non aggiunge il rendering OSC 66 né abilita protocolli grafici.

### Verifiche e costo

Le regressioni del runtime coprono:

- perdita originale al confine archivio/viewport;
- righe identiche realmente ripetute, Unicode wide, combining, ZWJ e flag;
- frammenti archiviati e mutabili nella stessa riga;
- ordine del testo e continuazioni soft-wrap, incluso clipping del cursore;
- repaint TUI, scroll alternate, ritorno al normal e resize del normal inattivo;
- otto cicli grow/shrink di sola altezza senza aumento dell'indice finale dello storico;
- riciclo del ring pieno e mantenimento del suffisso entro il budget;
- checkpoint normale invariato dopo repaint e resize.

Misura isolata su questa macchina, Release, 4.126 righe iniziali a 120 colonne ridotte a 80:

| Percorso | Tempo | Allocazioni sul thread |
| --- | ---: | ---: |
| Runtime Bivium completo | 64,8 ms | 55,79 MiB |
| Solo `Terminal.Resize` nativo | 19,4 ms | 20,86 MiB |

Sono misure singole su dati sintetici, non percentili o picco di memoria. Rispetto allo spike sono eliminati gli oggetti e le copie di testo per singola cella; gli snapshot sono limitati al viewport e i resize di sola altezza evitano la proiezione. Il costo aggiuntivo dei cambi di larghezza a scrollback pieno resta misurabile e richiede verifica interattiva; il debounce frontend esistente è di 50 ms.

Il test isolato `NarrowingClipsCursorLineEvenWithoutExternalArchive` continua a documentare il clipping nativo del cursore. Bivium conserva nell'archivio i frammenti che il proprio resize farebbe sparire, senza cambiare la geometria prodotta dalla libreria. La protezione introdotta riguarda `TerminalRuntimeService.ResizeSession`; non introduce intercettazioni dei resize interni richiesti da sequenze VT.

Mancano ancora accettazione interattiva del reflow con le applicazioni reali e verifica multipiattaforma. Codex e Claude sono verificati per la tastiera; btop è verificato per synchronized output. Il gate automatico del runtime è risolto; questo non dichiara completato l'intero piano post-upgrade.

## Lavoro restante

- Accettazione interattiva del reflow con le applicazioni reali e misure di latenza durante resize continui.
- Accettazione di Shift+Invio nelle combinazioni SSH/multiplexer usate; Codex e Claude locali sono verificati.
- Ulteriore matrice OSC 8 (URI lunghe, più link e alternate); synchronized output, palette dinamica e underline avanzate sono implementati.
- Configurazione e accettazione multipiattaforma delle shell OSC 133; la navigazione testuale con `Ctrl+Freccia su/giù` è implementata, mentre indicatori grafici ed export strutturato restano esclusi per decisione UX.
- Clipboard write testuale, toast, progresso e attenzione sono implementati; clipboard read, notifiche di sistema e pointer shape restano disabilitati per decisione UX.
- Sixel, Kitty Graphics e immagini iTerm2 sono esclusi per decisione di prodotto e restano disabilitati.
- Profiling e ottimizzazioni misurate sono completati; un protocollo snapshot compatto resta fuori scope finché un carico reale non ne dimostra la necessità.
- Recovery con pagina nascosta e reverse proxy di produzione, diagnosi della disconnessione originale e test multipiattaforma; recovery locale 1001 e circuito scaduto sono verificati.

## Verifica locale

```sh
dotnet test Bivium.Tests/Bivium.Tests.csproj --no-restore -m:1 -p:UseSharedCompilation=false
node --test Bivium.Tests/connection.test.mjs Bivium.Tests/terminal-virtualization.test.mjs Bivium.Tests/upload-selection.test.mjs
dotnet test Bivium.Tests/Bivium.Tests.csproj --no-restore --filter FullyQualifiedName~ReflowProvenanceSpikeTests -m:1 -p:UseSharedCompilation=false
```

Risultato finale dopo la fase 11: 124 test .NET superati e 3 script JavaScript superati. La fase 5 aggiunge 13 casi .NET e una prova browser dedicata; la fase 6 aggiunge sette casi .NET, verifiche renderer JavaScript e un'accettazione Chrome reale; la fase 7 aggiunge quattro casi .NET, la copertura JavaScript della scorciatoia e una prova Chrome reale attraverso archivio e schermata; la fase 8 aggiunge tre casi .NET e un'accettazione Chrome delle superfici UX; la fase 10 è chiusa senza codice; la fase 11 aggiunge la misura permanente di snapshot/JSON, la regressione byte UTF-8/OSC e una prova PTY reale. I 24 test dello spike sono inclusi nella suite .NET; passano inoltre i 27 casi `ByteWriteParityTests` del fork. Compilazione riuscita senza warning o errori nel progetto Bivium; la misura isolata Release del reflow e quelle della fase 11 sono riportate sopra. Verificate inoltre le prove browser reali di tastiera, recovery 1001, circuito scaduto, synchronized output, colori, navigazione OSC 133, UX dei protocolli terminali e percorso PTY byte. Nessun comando Git/GH è stato eseguito e nessun sorgente del fork è stato modificato.

### Accettazione browser riproducibile

I due script di accettazione vanno eseguiti esplicitamente, su un'istanza Bivium isolata. Non avviano Codex o Claude e non inviano richieste a servizi di modelli.

1. Avviare Bivium con home e data directory temporanee, usando l'endpoint Kestrel `http://127.0.0.1:5186`.
2. Avviare Chrome con un profilo temporaneo e remote debugging su `127.0.0.1:9226`.
3. Avviare `node Bivium.Tests/terminal-reconnect-proxy.mjs`, che ascolta esclusivamente su loopback alla porta 5187.
4. Eseguire `node Bivium.Tests/terminal-browser-acceptance.mjs` per tastiera e recovery 1001.
5. Eseguire `node Bivium.Tests/terminal-browser-acceptance.mjs --expired-circuit` per indisponibilità di 205 secondi e fallback dopo scadenza del circuito.
6. Terminare i processi di test e rimuovere, se desiderato, le directory temporanee.

Il driver richiede Node con `fetch` e `WebSocket` globali (verificato con Node 26). Le porte sono configurabili con `BIVIUM_TEST_PORT`, `BIVIUM_TEST_PROXY_PORT` e `BIVIUM_TEST_CDP_PORT`. Il proxy e i suoi endpoint di controllo sono solo fixture di test e non fanno parte dell'applicazione.
