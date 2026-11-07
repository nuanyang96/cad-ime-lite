;;; ============================================================
;;;  autoload.lsp  -  load CadImeSwitch.lsp AND start its reactor
;;;
;;;  Why this file exists (2026-10-26):
;;;    Loading CadImeSwitch.lsp only DEFINES the commands; the command
;;;    reactor is registered by (c:imesw-on). Auto-loading the lsp alone
;;;    therefore leaves Hits=0 and the IME never switches.
;;;    This loader does both, and never throws (a failure here must not
;;;    break drawing open).
;;;
;;;  ASCII ONLY: an AutoLISP file with Chinese text may fail to load.
;;; ============================================================
(vl-catch-all-apply
  (quote load)
  (list "C:/CadImeSwitch/CadImeSwitch.lsp"))
(vl-catch-all-apply (quote c:imesw-on) nil)
(princ)
