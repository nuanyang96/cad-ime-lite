;;; ============================================================
;;;  CadImeSwitch - AutoCAD command logger + language state reporter
;;;
;;;  Pure ASCII on purpose.  (A single non-ascii byte anywhere in
;;;  this file can make AutoCAD fail to load it - that rule is not
;;;  negotiable.  Keep every comment in english.)
;;;
;;;  What it does:
;;;    - a command reactor watches every command
;;;    - it decides whether that command needs chinese input
;;;    - it writes the wanted state to
;;;         %LOCALAPPDATA%\CadImeSwitch\status.txt
;;;      lines:
;;;         1 = drawing date/time number
;;;         2 = command name
;;;         3 = IDLE | EN | CN     (for reference)
;;;         4 = reactor hit counter
;;;         5 = STATE=CN | STATE=EN   <-- the tray app reads this
;;;    - the tray app notices STATE changes and switches the input
;;;      LANGUAGE of the AutoCAD window (no IME hotkey involved).
;;;
;;;  Commands:
;;;    (c:imesw-on)      start          (c:imesw-off)     stop
;;;    (c:imesw-test)    write a test line
;;;    (c:imesw-state)   show what it thinks
;;;    (c:imesw-sync)    note: input is ENGLISH now
;;;    (c:imesw-sync-cn) note: input is CHINESE now
;;; ============================================================

(vl-load-com)

;; ------------------------------------------------------------------
;;  STATE CHANNEL: the Windows REGISTRY, not a file.
;;
;;  *** THIS TOOK A WHOLE DAY TO FIND - DO NOT CHANGE IT BACK ***
;;
;;  What was tried and MEASURED to fail on this machine
;;  (Windows 11 + AutoCAD 2018/2024):
;;    - AutoLISP writing %LOCALAPPDATA%\CadImeSwitch\status.txt:
;;      (open ...) returned a HANDLE but the file stayed 0 bytes and
;;      (findfile ...) kept returning nil.  Silent failure.
;;    - AutoLISP writing %TEMP%\CadImeSwitch\status.txt: same.
;;    - AutoLISP writing %TEMP%\CadImeSwitch-status.txt (flat): same.
;;    - acaddoc.lsp writing loadlog.txt into the same folder: WORKS.
;;    - AutoLISP writing an mktemp file into %TEMP%: WORKS.
;;  So file output from a COMMAND-TIME reactor is the unreliable part.
;;
;;  What WORKS and is used now:
;;      (vl-registry-write "HKEY_CURRENT_USER\\Software\\CadImeSwitch" ...)
;;  Verified live: the key appeared in the registry within seconds of the
;;  AutoLISP call, with State=CN.  The tray reads the same key.
;; ------------------------------------------------------------------
(setq *imesw-reg-path* "HKEY_CURRENT_USER\\Software\\CadImeSwitch")

(defun imesw-reg (name value)
  (vl-catch-all-apply 'vl-registry-write (list *imesw-reg-path* name value)))

;; ------------------------------------------------------------------
;;  HISTORY: MTEXT / TEXT / DTEXT / MTEDIT were REMOVED from this list on
;;  2026-10-10 because the reactor fires while the command name is still
;;  being typed (it recognises TEXT at "TE"), so the IME switched to CN
;;  mid-typing and the rest of the letters were eaten by the IME.
;;  No delay fixed that, and injecting DELAY from a reactor broke SAVE.
;;
;;  Then they were ADDED BACK the same day, at the user request: they want
;;  the in-place text editor to start in chinese.  The tray CANNOT detect
;;  that editor by itself - AutoCAD draws it inside the drawing view and
;;  creates no window at all (17 window snapshots compared; see the project
;;  notes section 4.5).  So the only route is this list + the reactor.
;;
;;  KNOWN COST (user accepted it): typing TEXT/MTEXT may switch the IME to
;;  chinese before the name is finished, so the command may need Enter.
;;
;;  To go back to "command names stay english": delete these four names
;;  (the tray defaults to english when there is no LISP state).
;; ------------------------------------------------------------------
(setq *imesw-cn-commands*
  '("MTEXT" "MTEDIT" "TEXT" "DTEXT" "DDEDIT" "TEXTEDIT" "DDATTE" "EATTEDIT"
    "ATTDEF" "ATTEDIT" "BATTMAN" "MLEADER" "MLEADEREDIT"
    "DIM" "DIMLINEAR" "DIMALIGNED" "DIMANGULAR" "DIMDIAMETER" "DIMRADIUS"
    "DIMORDINATE" "DIMBASELINE" "DIMCONTINUE" "DIMSPACE" "DIMSTYLE"
    "QDIM" "TOLERANCE" "LEADER" "QLEADER" "TABLE" "TABLEDIT" "STYLE"
    "MSTYLE" "SPELL" "FIND" "PROPERTIES" "LAYER" "RENAME" "GROUP"
    "BLOCK" "BEDIT" "WBLOCK" "FIELD" "TXT2MTXT"))

;; ------------------------------------------------------------------
;;  These MUST be initialised before anything can use them.
;;
;;  Bug history (2026-10-10): when the callback was hardened, this line
;;  got lost, so *imesw-hits* started out as nil.  Every write then threw
;;  "fixnump: nil" inside the file-writing progn, which aborted it before
;;  close/rename - so the status file was NEVER written and the tray had
;;  nothing to read.  The feature looked dead while the code "looked fine".
;; ------------------------------------------------------------------
(setq *imesw-hits* 0)
(setq *imesw-ime-cn* nil)

;; ------------------------------------------------------------------
;;  *** RULE 1: a reactor callback must NEVER let an error escape. ***
;;
;;  Why (learned the hard way, 2026-10-10):
;;    A command reactor callback runs INSIDE the user's command.  Any
;;    error that escapes it is reported against that command, so the
;;    user sees their own command fail.  On a real machine this showed
;;    up as SAVE breaking:
;;        Command: _QSAVE
;;        ; error: bad argument type: numberp: nil
;;        ; error: bad argument type: fixnump: nil
;;    The drawing was not damaged, but the save command was interrupted.
;;
;;  *** RULE 2: keep the callback free of ANY avoidable dependency. ***
;;
;;    The old line 1 of the status file was
;;        (rtos (getvar "DATE") 2 8)
;;    which is the likely source of that nil: getvar can return nothing
;;    in a reactor context, and rtos then raises numberp: nil.
;;    That timestamp was only ever a debugging aid - the tray app reads
;;    line 5 (STATE=...) and nothing else.  So it is GONE: line 1 is now
;;    the hit counter, which cannot fail.
;;
;;  RULE 3: never inject commands (command / vl-cmdf / sendkeys) from a
;;  callback - that corrupts the running command too.
;; ------------------------------------------------------------------

;; ------------------------------------------------------------------
;;  ERROR SAFETY WITHOUT lambda
;;
;;  *** THIS WAS THE REAL BUG ALL ALONG (found 2026-10-10) ***
;;
;;  The original code ran every callback body through
;;      (imesw-safe (lambda () ...))
;;  and imesw-safe did
;;      (vl-catch-all-apply 'vl-catch-all-apply (list thunk))
;;  On AutoCAD 2018 this NEVER EXECUTED THE BODY.  Measured proof:
;;      (imesw-safe (lambda () (imesw-reg "State" "ZZ")))
;;      -> returned T, but the registry value stayed "CN" (not "ZZ"),
;;      while calling imesw-write-raw directly DID write.
;;  So every command callback silently did nothing: no state was ever
;;  reported, the tray never saw CN, and the whole feature looked dead
;;  while every file, loader and reactor looked perfectly healthy.
;;
;;  Fix: do not pass lambdas around.  Each function wraps its own body
;;  with vl-catch-all-apply and ignores the result.  That still keeps
;;  RULE 1 (nothing may escape a reactor callback) but actually runs.
;; ------------------------------------------------------------------

(defun imesw-num-string (n / r)
  (setq r (vl-catch-all-apply 'itoa (list n)))
  (if (or (vl-catch-all-error-p r) (null r)) "0" r))

(defun imesw-hits-up ( / h)
  (setq h (if (numberp *imesw-hits*) *imesw-hits* 0))
  (setq *imesw-hits* (1+ h)))

(defun imesw-write-raw (cmd state / s cn)
  ;; Write the state to the registry.  Verified working on this machine.
  (setq s  (if state state "IDLE"))
  (setq cn (if *imesw-ime-cn* "CN" "EN"))
  (imesw-reg "State"    cn)
  (imesw-reg "Command"  (if cmd cmd "?"))
  (imesw-reg "Line3"    s)
  (imesw-reg "Hits"     (imesw-num-string *imesw-hits*))
  (imesw-reg "Time"     (imesw-num-string (fix (* 86400 (getvar "DATE")))))
  (imesw-reg "Version"  "2")
  T)

;; public entry: never throws.  NOTE: no lambda - see the comment above.
(defun imesw-write (cmd state)
  (vl-catch-all-apply 'imesw-write-raw (list cmd state))
  T)

(defun imesw-is-cn (cmd / r)
  (setq r (vl-catch-all-apply 'member (list (strcase cmd) *imesw-cn-commands*)))
  (if (vl-catch-all-error-p r) nil (if r T nil)))

;; ---- command starts ----
;; (there used to be a (vl-cmdf "_.DELAY" ...) here - NEVER put it back:
;;  injecting a command from a callback corrupts the running command.)
(defun imesw-cmd-start (rea cmd / name want)
  (vl-catch-all-apply
    'imesw-cmd-start-body (list cmd))
  T)

(defun imesw-cmd-start-body (cmd / name want)
  (imesw-hits-up)
  (setq name (car cmd))
  (setq want (imesw-is-cn name))
  (setq *imesw-ime-cn* want)
  (imesw-write-raw name (if want "CN" "EN")))

;; ---- command ends ----
(defun imesw-cmd-end (rea cmd)
  (vl-catch-all-apply 'imesw-cmd-end-body (list cmd))
  T)

(defun imesw-cmd-end-body (cmd)
  (setq *imesw-ime-cn* nil)
  (imesw-write-raw (car cmd) "IDLE"))

(defun imesw-cmd-failed (rea cmd)
  (vl-catch-all-apply 'imesw-cmd-quiet (list))
  T)

(defun imesw-cmd-cancelled (rea cmd)
  (vl-catch-all-apply 'imesw-cmd-quiet (list))
  T)

(defun imesw-cmd-quiet ( / )
  (setq *imesw-ime-cn* nil)
  (imesw-write-raw "?" "IDLE"))

(defun c:imesw-on ( / )
  (if *imesw-reactor* (c:imesw-off))
  (setq *imesw-reactor*
    (vlr-command-reactor
      "CadImeSwitch"
      '((:vlr-commandWillStart . imesw-cmd-start)
        (:vlr-commandEnded     . imesw-cmd-end)
        (:vlr-commandFailed    . imesw-cmd-failed)
        (:vlr-commandCancelled . imesw-cmd-cancelled))))
  (imesw-write "-" "IDLE")
  (princ "\n[CadImeSwitch] ON. state: registry ")
  (princ *imesw-reg-path*)
  (princ))

(defun c:imesw-off ( / )
  (if *imesw-reactor*
    (progn (vlr-remove *imesw-reactor*) (setq *imesw-reactor* nil)))
  (princ "\n[CadImeSwitch] OFF.")
  (princ))

(defun c:imesw-test ( / )
  (setq *imesw-hits* (1+ (if (numberp *imesw-hits*) *imesw-hits* 0)))
  (imesw-write "TEST" "IDLE")
  (princ "\n[CadImeSwitch] test written to registry: ")
  (princ *imesw-reg-path*)
  (princ))

;; tell the logger what the input state is right now
(defun c:imesw-sync ( / )
  (setq *imesw-ime-cn* nil)
  (imesw-write "-" "IDLE")
  (princ "\n[CadImeSwitch] noted: ENGLISH now.")
  (princ))

(defun c:imesw-sync-cn ( / )
  (setq *imesw-ime-cn* T)
  (imesw-write "-" "CN")
  (princ "\n[CadImeSwitch] noted: CHINESE now.")
  (princ))

(defun c:imesw-state ( / )
  (princ (strcat "\n[CadImeSwitch] I think the input is: "
                 (if *imesw-ime-cn* "CHINESE" "ENGLISH")))
  (princ))

(c:imesw-on)
(princ)
