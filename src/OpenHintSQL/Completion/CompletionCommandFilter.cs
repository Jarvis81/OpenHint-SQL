using System;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Text.Editor;
using OpenHintSQL.Connection;
using OpenHintSQL.Context;
using OpenHintSQL.Providers;
using OpenHintSQL.Schema;
using OpenHintSQL.Snippets;
using OpenHintSQL.UI;
using OpenHintSQL.Utils;

namespace OpenHintSQL.Completion
{
    /// <summary>
    /// Command filter that intercepts editor keystrokes to provide completion popup
    /// management and snippet expansion. Implements <see cref="IOleCommandTarget"/>
    /// to integrate with the VS editor command chain.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This filter is inserted into the command chain by <see cref="CompletionViewCreationListener"/>.
    /// It handles pre-processing (Tab for snippets, Enter/Esc/Up/Down for popup navigation)
    /// and post-processing (character typing, backspace to trigger/update/dismiss completion).
    /// </para>
    /// <para>
    /// CRITICAL: All code paths are wrapped in try/catch to ensure that exceptions from
    /// our extension never prevent SSMS from functioning normally.
    /// </para>
    /// </remarks>
    internal sealed class CompletionCommandFilter : IOleCommandTarget
    {
        private readonly IWpfTextView _textView;
        private readonly ICompletionBroker _nativeCompletionBroker;
        private CompletionPopup _popup;
        private bool _isDisposed;
        private DispatcherTimer _nativeCompletionSuppressTimer;
        private DateTime _nativeCompletionSuppressUntilUtc;
        private static readonly Regex JoinSuggestionTailBoundaryPattern = new Regex(
            @"\b(?:INNER\s+JOIN|LEFT\s+(?:OUTER\s+)?JOIN|RIGHT\s+(?:OUTER\s+)?JOIN|FULL\s+(?:OUTER\s+)?JOIN|CROSS\s+JOIN|JOIN|WHERE|GROUP\s+BY|ORDER\s+BY|HAVING|UNION|EXCEPT|INTERSECT)\b|;",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// The next command target in the chain. Set by the caller after AddCommandFilter.
        /// </summary>
        public IOleCommandTarget NextTarget { get; set; }

        /// <summary>
        /// Minimum prefix length required to trigger completion.
        /// </summary>
        private const int MinPrefixLength = 2;
        private const int NativeCompletionSuppressDurationMs = 5000;

        /// <summary>
        /// Creates a new command filter for the given text view.
        /// </summary>
        /// <param name="textView">The WPF text view to attach to.</param>
        public CompletionCommandFilter(
            IWpfTextView textView,
            ICompletionBroker nativeCompletionBroker = null)
        {
            _textView = textView ?? throw new ArgumentNullException(nameof(textView));
            _nativeCompletionBroker = nativeCompletionBroker;

            // Create the completion popup (must be on UI thread, which we are during view creation)
            _popup = new CompletionPopup();
            _popup.ItemAccepted += OnItemAccepted;

            // Dismiss popup when the editor loses focus
            _textView.LostAggregateFocus += OnEditorLostFocus;
            _textView.Closed += OnEditorClosed;

            // Refresh the popup once the schema finishes loading in the background — this is
            // what makes the very first table-completion attempt work after a cold cache.
            SchemaCache.OnSchemaLoaded += OnSchemaReady;
            SchemaCache.OnSchemaLoadFailed += OnSchemaLoadFailed;
            DatabaseListCache.OnDatabasesLoaded += OnDatabaseListReady;
            DatabaseListCache.OnDatabaseListLoadFailed += OnDatabaseListLoadFailed;
            ProcedureCache.OnProceduresLoaded += OnProcedureListReady;
            ProcedureCache.OnProcedureLoadFailed += OnProcedureListLoadFailed;
        }

        /// <summary>
        /// Called by SchemaCache (off the UI thread) whenever a schema load finishes.
        /// Re-triggers completion if the popup is currently visible, so the freshly
        /// loaded tables show up without the user having to type more.
        /// </summary>
        private void OnSchemaReady(string key, DatabaseSchema schema)
        {
            RefreshPopupAfterSchemaLoad();
        }

        private void OnSchemaLoadFailed(string key, string message)
        {
            RefreshPopupAfterSchemaLoad();
        }

        private void OnDatabaseListReady(string key, DatabaseList databases)
        {
            RefreshPopupAfterSchemaLoad();
        }

        private void OnDatabaseListLoadFailed(string key, string message)
        {
            RefreshPopupAfterSchemaLoad();
        }

        private void OnProcedureListReady(string key, ProcedureList procedures)
        {
            RefreshPopupAfterSchemaLoad();
        }

        private void OnProcedureListLoadFailed(string key, string message)
        {
            RefreshPopupAfterSchemaLoad();
        }

        private void RefreshPopupAfterSchemaLoad()
        {
            try
            {
                if (_isDisposed || _textView == null || _textView.IsClosed)
                    return;

                // Marshal to the UI thread — popup access must happen there.
                var dispatcher = _textView.VisualElement?.Dispatcher;
                if (dispatcher == null)
                    return;

                dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        if (_isDisposed || !IsPopupVisible())
                            return;
                        TriggerCompletion(allowEmptyPrefix: true, forceRefresh: true);
                    }
                    catch (Exception ex)
                    {
                        Logger.Error("OnSchemaReady UI-thread refresh failed", ex);
                    }
                }));
            }
            catch (Exception ex)
            {
                Logger.Error("Schema load popup refresh failed", ex);
            }
        }

        /// <summary>
        /// Queries the status of commands. Delegates to the next target in the chain.
        /// </summary>
        public int QueryStatus(ref Guid pguidCmdGroup, uint cCmds, OLECMD[] prgCmds, IntPtr pCmdText)
        {
            try
            {
                if (NextTarget != null)
                    return NextTarget.QueryStatus(ref pguidCmdGroup, cCmds, prgCmds, pCmdText);
            }
            catch (Exception ex)
            {
                Logger.Error("CompletionCommandFilter.QueryStatus failed", ex);
            }

            return (int)Constants.OLECMDERR_E_NOTSUPPORTED;
        }

        /// <summary>
        /// Executes commands, performing pre- and post-processing for completion.
        /// </summary>
        public int Exec(ref Guid pguidCmdGroup, uint nCmdID, uint nCmdexecopt, IntPtr pvaIn, IntPtr pvaOut)
        {
            try
            {
                // Diagnostic logging to find exact command groups/IDs in different SSMS versions
                // Only log interesting ones to avoid noise: Tab=2, Enter=3, TypeChar=1, Backspace=6
                if (nCmdID == 1 || nCmdID == 2 || nCmdID == 3 || nCmdID == 4 || nCmdID == 6)
                {
                    Logger.Diagnostic($"Exec: group={pguidCmdGroup}, ID={nCmdID}");
                }

                // ─── PRE-PROCESSING ───────────────────────────────────────
                int preResult = HandlePreProcessing(pguidCmdGroup, nCmdID, pvaIn);
                if (preResult == VSConstants.S_OK)
                    return VSConstants.S_OK; // Command was handled; don't pass to next target

                // ─── PASS TO NEXT HANDLER ─────────────────────────────────
                int hr = PassToNextTarget(ref pguidCmdGroup, nCmdID, nCmdexecopt, pvaIn, pvaOut);

                // ─── POST-PROCESSING ──────────────────────────────────────
                if (pguidCmdGroup == VSConstants.VSStd2K)
                {
                    HandlePostProcessing((VSConstants.VSStd2KCmdID)nCmdID, pvaIn);
                }

                return hr;
            }
            catch (Exception ex)
            {
                Logger.Error("CompletionCommandFilter.Exec failed", ex);
            }

            // For non-VS commands, pass through
            return PassToNextTarget(ref pguidCmdGroup, nCmdID, nCmdexecopt, pvaIn, pvaOut);
        }

        /// <summary>
        /// Handles pre-processing of commands (before the character is inserted).
        /// Returns S_OK if the command was consumed and should not be passed further.
        /// </summary>
        private int HandlePreProcessing(Guid pguidCmdGroup, uint nCmdID, IntPtr pvaIn)
        {
            try
            {
                if (pguidCmdGroup == VSConstants.VSStd2K)
                {
                    var cmdId = (VSConstants.VSStd2KCmdID)nCmdID;
                    switch (cmdId)
                    {
                        case VSConstants.VSStd2KCmdID.TAB:
                            return HandleTab();

                        case VSConstants.VSStd2KCmdID.TYPECHAR:
                            if (GetTypedChar(pvaIn) == '\t')
                                return HandleTab();
                            break;

                        case VSConstants.VSStd2KCmdID.RETURN:
                            return HandleEnter();

                        case VSConstants.VSStd2KCmdID.CANCEL:
                            return HandleEscape();

                        case VSConstants.VSStd2KCmdID.UP:
                            return HandleUpDown(isUp: true);

                        case VSConstants.VSStd2KCmdID.DOWN:
                            return HandleUpDown(isUp: false);

                        case VSConstants.VSStd2KCmdID.SHOWMEMBERLIST:
                        case VSConstants.VSStd2KCmdID.COMPLETEWORD:
                            return HandleCompletionList();
                    }
                }
                else if (pguidCmdGroup == VSConstants.GUID_VSStandardCommandSet97)
                {
                    // SSMS 18/19 might use VSStd97 for some basic navigation/edit commands
                    switch (nCmdID)
                    {
                        case 1: // Common ID for Tab in some shells? Actually Tab is usually VSStd2K.
                                // But if SSMS 18 sends Tab via VSStd97, we catch it here.
                                // Let's check common VSStd97 IDs: 3=Return, 40=Tab?
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("HandlePreProcessing failed", ex);
            }

            return VSConstants.E_FAIL; // Not handled; continue to next target
        }

        /// <summary>
        /// Handles post-processing of commands (after the character has been inserted).
        /// </summary>
        private void HandlePostProcessing(VSConstants.VSStd2KCmdID cmdId, IntPtr pvaIn)
        {
            try
            {
                switch (cmdId)
                {
                    case VSConstants.VSStd2KCmdID.TYPECHAR:
                        HandleTypeChar(pvaIn);
                        break;

                    case VSConstants.VSStd2KCmdID.BACKSPACE:
                    case VSConstants.VSStd2KCmdID.DELETE:
                        HandleBackspaceDelete();
                        break;
                }
            }
            catch (Exception ex)
            {
                Logger.Error("HandlePostProcessing failed", ex);
            }
        }

        // ═══════════════════════════════════════════════════════════════
        //  PRE-PROCESSING HANDLERS
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Handles Tab key: first tries snippet expansion, then popup selection, then passes through.
        /// </summary>
        private int HandleTab()
        {
            try
            {
                // 0. SELECT * → expand all columns
                string fullText0 = _textView.GetAllText();
                int caretOffset0 = _textView.GetCaretPosition();
                if (IsAfterStarInSelect(fullText0, caretOffset0))
                {
                    DismissPopup();
                    if (TryExpandStarToColumns(fullText0, caretOffset0))
                        return VSConstants.S_OK;
                    return VSConstants.E_FAIL;
                }

                // 1. Try snippet expansion
                string wordBeforeCaret = _textView.GetWordBeforeCaret();
                if (!string.IsNullOrEmpty(wordBeforeCaret))
                {
                    if (SnippetProvider.Instance.TryGetSnippet(wordBeforeCaret, out SnippetDefinition snippet))
                    {
                        Logger.Diagnostic($"Expanding snippet: '{wordBeforeCaret}' -> '{snippet.Title}'");
                        ExpandSnippet(wordBeforeCaret, snippet);
                        DismissPopup();
                        return VSConstants.S_OK;
                    }
                }

                // 2. If popup is visible with a selection, accept the item
                if (IsPopupVisible() && _popup.SelectedItem != null)
                {
                    AcceptSelectedItem();
                    return VSConstants.S_OK;
                }
            }
            catch (Exception ex)
            {
                Logger.Error("HandleTab failed", ex);
            }

            // 3. Not handled — pass through (normal Tab behavior)
            return VSConstants.E_FAIL;
        }

        /// <summary>
        /// Handles Enter key: accepts selected popup item if visible, otherwise passes through.
        /// </summary>
        private int HandleEnter()
        {
            try
            {
                if (IsPopupVisible() && _popup.SelectedItem != null)
                {
                    AcceptSelectedItem();
                    return VSConstants.S_OK;
                }
            }
            catch (Exception ex)
            {
                Logger.Error("HandleEnter failed", ex);
            }

            return VSConstants.E_FAIL;
        }

        /// <summary>
        /// Handles Escape key: dismisses the popup if visible.
        /// </summary>
        private int HandleEscape()
        {
            try
            {
                if (IsPopupVisible())
                {
                    DismissPopup();
                    return VSConstants.S_OK;
                }
            }
            catch (Exception ex)
            {
                Logger.Error("HandleEscape failed", ex);
            }

            return VSConstants.E_FAIL;
        }

        /// <summary>
        /// Handles Up/Down arrow keys: navigates popup selection if visible.
        /// </summary>
        private int HandleUpDown(bool isUp)
        {
            try
            {
                if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
                    return VSConstants.E_FAIL;

                if (IsPopupVisible())
                {
                    if (isUp)
                        _popup.SelectPrevious();
                    else
                        _popup.SelectNext();

                    return VSConstants.S_OK;
                }
            }
            catch (Exception ex)
            {
                Logger.Error("HandleUpDown failed", ex);
            }

            return VSConstants.E_FAIL;
        }

        /// <summary>
        /// Handles explicit completion requests such as Ctrl+Space.
        /// </summary>
        private int HandleCompletionList()
        {
            try
            {
                TriggerCompletion(allowEmptyPrefix: true);
                return IsPopupVisible() ? VSConstants.S_OK : VSConstants.E_FAIL;
            }
            catch (Exception ex)
            {
                Logger.Error("HandleCompletionList failed", ex);
                return VSConstants.E_FAIL;
            }
        }

        // ═══════════════════════════════════════════════════════════════
        //  POST-PROCESSING HANDLERS
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Handles a typed character: triggers, updates, or dismisses completion as appropriate.
        /// </summary>
        private void HandleTypeChar(IntPtr pvaIn)
        {
            try
            {
                char typedChar = GetTypedChar(pvaIn);
                if (typedChar == '\0')
                    return;

                if (char.IsLetterOrDigit(typedChar) || typedChar == '_')
                {
                    TriggerCompletion();
                }
                else if (typedChar == '.')
                {
                    // Dot trigger: alias-qualified column list, no prefix needed.
                    TriggerCompletion(allowEmptyPrefix: true);
                }
                else if (typedChar == '[')
                {
                    string fullText = _textView.GetAllText();
                    int caretOffset = _textView.GetCaretPosition();
                    var context = SqlContextParser.GetContext(fullText, caretOffset);
                    if (context == SqlContext.UseDatabase ||
                        context == SqlContext.SpHelp ||
                        context == SqlContext.SpHelpText)
                    {
                        TriggerCompletion(allowEmptyPrefix: true, forceRefresh: true);
                    }
                    else
                    {
                        DismissPopup();
                    }
                }
                else if (char.IsWhiteSpace(typedChar))
                {
                    // Whitespace right after a clause keyword (FROM, JOIN, EXEC, …)
                    // should pop the table/proc list immediately — that's the
                    // Otherwise dismiss.
                    string fullText = _textView.GetAllText();
                    int caretOffset = _textView.GetCaretPosition();
                    if (SqlContextParser.TryGetClauseAtCaret(fullText, caretOffset, out _))
                    {
                        TriggerCompletion(allowEmptyPrefix: true);
                    }
                    else
                    {
                        DismissPopup();
                    }
                }
                else if (typedChar == '*')
                {
                    string fullText = _textView.GetAllText();
                    int caretOffset = _textView.GetCaretPosition();
                    if (IsAfterStarInSelect(fullText, caretOffset))
                        ShowStarExpansionHint();
                    else
                        DismissPopup();
                }
                else
                {
                    // Delimiter character (semicolon, comma, etc.) — dismiss popup
                    DismissPopup();
                }
            }
            catch (Exception ex)
            {
                Logger.Error("HandleTypeChar failed", ex);
            }
        }

        /// <summary>
        /// Handles Backspace/Delete: re-triggers with the shorter prefix or dismisses.
        /// </summary>
        private void HandleBackspaceDelete()
        {
            try
            {
                string word = _textView.GetWordBeforeCaret();

                if (string.IsNullOrEmpty(word) || word.Length < MinPrefixLength)
                {
                    DismissPopup();
                }
                else
                {
                    TriggerCompletion();
                }
            }
            catch (Exception ex)
            {
                Logger.Error("HandleBackspaceDelete failed", ex);
            }
        }

        // ═══════════════════════════════════════════════════════════════
        //  COMPLETION TRIGGER
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Triggers or updates the completion popup based on the current word before the caret.
        /// </summary>
        private void TriggerCompletion() => TriggerCompletion(allowEmptyPrefix: false);

        /// <summary>
        /// Triggers or updates the completion popup. When <paramref name="allowEmptyPrefix"/>
        /// is true, the <see cref="MinPrefixLength"/> gate is skipped — the engine itself
        /// decides whether an empty prefix makes sense for the current SQL context.
        /// </summary>
        private void TriggerCompletion(bool allowEmptyPrefix, bool forceRefresh = false)
        {
            try
            {
                string prefix = _textView.GetWordBeforeCaret();
                string fullText = _textView.GetAllText();
                int caretOffset = _textView.GetCaretPosition();
                var context = SqlContextParser.GetContext(fullText, caretOffset);
                bool hasContextPopup = IsPopupVisible() || context == SqlContext.UseDatabase;

                if (!allowEmptyPrefix &&
                    !hasContextPopup &&
                    (string.IsNullOrEmpty(prefix) || prefix.Length < MinPrefixLength))
                {
                    DismissPopup();
                    return;
                }

                // If the popup is already visible AND the user is still narrowing a word,
                // just update the filter. Empty-prefix triggers (FROM␣ / JOIN␣ / dot) always
                // need a fresh query — they swap the popup's contents entirely.
                if (!forceRefresh && IsPopupVisible() && !string.IsNullOrEmpty(prefix) && !_popup.IsShowingStatus)
                {
                    StartNativeCompletionSuppression();
                    _popup.UpdateFilter(prefix);

                    if (_popup.SelectedItem == null && !_popup.HasItems)
                    {
                        DismissPopup();
                    }
                    return;
                }

                // Get connection info for schema completion
                string server = null;
                string database = null;
                string connectionString = null;

                try
                {
                    var connInfo = ConnectionTracker.GetActiveConnection();
                    if (connInfo != null)
                    {
                        server = connInfo.Server;
                        database = connInfo.Database;
                        connectionString = connInfo.ConnectionString;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn($"Failed to get connection info: {ex.Message}");
                }

                // Generate completion items
                var items = CompletionEngine.GetCompletionItems(
                    prefix, fullText, caretOffset, server, database, connectionString);

                Logger.Diagnostic($"Completion generated {items?.Count ?? 0} item(s) for prefix='{prefix}'");

                if (items != null && items.Count > 0)
                {
                    StartNativeCompletionSuppression();
                    _popup.Show(_textView, items);
                }
                else
                {
                    DismissPopup();
                }
            }
            catch (Exception ex)
            {
                Logger.Error("TriggerCompletion failed", ex);
                DismissPopup();
            }
        }

        // ═══════════════════════════════════════════════════════════════
        //  SNIPPET EXPANSION
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Expands a snippet by replacing the shortcut text with the expansion,
        /// then positions the caret at the $cursor$ marker.
        /// </summary>
        private void ExpandSnippet(string shortcut, SnippetDefinition snippet)
        {
            try
            {
                string expansion = snippet.Expansion;
                if (string.IsNullOrEmpty(expansion))
                    return;

                // Replace \n with actual newlines
                expansion = expansion.Replace("\\n", Environment.NewLine);

                // Find $cursor$ position before removing it
                const string cursorMarker = "$cursor$";
                int cursorOffset = expansion.IndexOf(cursorMarker, StringComparison.Ordinal);

                // Remove the $cursor$ marker from the expansion text
                string finalText;
                if (cursorOffset >= 0)
                {
                    finalText = expansion.Remove(cursorOffset, cursorMarker.Length);
                }
                else
                {
                    finalText = expansion;
                    cursorOffset = finalText.Length; // Place cursor at end if no marker
                }

                // Calculate the span to replace (the shortcut word)
                int wordStart = _textView.GetWordBeforeCaretStart();
                int wordLength = shortcut.Length;

                // Replace the shortcut with the expanded text
                if (_textView.ReplaceSpan(wordStart, wordLength, finalText))
                {
                    // Position the caret at $cursor$ location
                    int newCaretPos = wordStart + cursorOffset;
                    var snapshot = _textView.TextSnapshot;

                    if (newCaretPos >= 0 && newCaretPos <= snapshot.Length)
                    {
                        var point = new Microsoft.VisualStudio.Text.SnapshotPoint(snapshot, newCaretPos);
                        _textView.Caret.MoveTo(point);
                        _textView.Caret.EnsureVisible();
                    }

                    // If the snippet landed the caret right after a clause keyword
                    // (e.g. `ssf` → `SELECT * FROM `), immediately pop the table list.
                    string fullText = _textView.GetAllText();
                    int caretAfter = _textView.GetCaretPosition();
                    if (SqlContextParser.TryGetClauseAtCaret(fullText, caretAfter, out _))
                    {
                        TriggerCompletion(allowEmptyPrefix: true);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("ExpandSnippet failed", ex);
            }
        }

        // ═══════════════════════════════════════════════════════════════
        //  ITEM ACCEPTANCE
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Accepts the currently selected completion item: replaces the typed prefix
        /// with the item's insert text.
        /// </summary>
        /// <remarks>
        /// <see cref="CompletionPopup.AcceptSelected"/> raises <see cref="CompletionPopup.ItemAccepted"/>,
        /// which we've already wired to <see cref="OnItemAccepted"/> → <see cref="InsertCompletionItem"/>.
        /// We must NOT also insert here, otherwise multi-word items (e.g. "ORDER BY",
        /// "IF EXISTS") get inserted twice and produce things like "ORDER ORDER BY".
        /// </remarks>
        private void AcceptSelectedItem()
        {
            try
            {
                _popup.AcceptSelected();
            }
            catch (Exception ex)
            {
                Logger.Error("AcceptSelectedItem failed", ex);
            }
        }

        /// <summary>
        /// Called when an item is accepted via mouse click in the popup.
        /// </summary>
        private void OnItemAccepted(CompletionItemData item)
        {
            try
            {
                if (item != null)
                {
                    InsertCompletionItem(item);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("OnItemAccepted failed", ex);
            }
        }

        /// <summary>
        /// Inserts a completion item into the editor, replacing the current prefix.
        /// </summary>
        private void InsertCompletionItem(CompletionItemData item)
        {
            try
            {
                string insertText = item.InsertText ?? item.Text;
                if (string.IsNullOrEmpty(insertText))
                    return;

                int caretPos = _textView.GetCaretPosition();
                int replaceStart = IsObjectReferenceCompletion(item)
                    ? GetObjectReferenceBeforeCaretStart(caretPos)
                    : _textView.GetWordBeforeCaretStart();
                int replaceEnd = item.Kind == CompletionItemKind.JoinSuggestion
                    ? GetJoinSuggestionReplaceEnd(caretPos)
                    : caretPos;
                int wordLength = Math.Max(0, replaceEnd - replaceStart);

                if (wordLength >= 0)
                {
                    _textView.ReplaceSpan(replaceStart, wordLength, insertText);
                }

                Logger.Diagnostic($"Inserted completion: '{insertText}'");
            }
            catch (Exception ex)
            {
                Logger.Error("InsertCompletionItem failed", ex);
            }
        }

        // ═══════════════════════════════════════════════════════════════
        //  HELPERS
        // ═══════════════════════════════════════════════════════════════

        private int GetObjectReferenceBeforeCaretStart(int caretPos)
        {
            try
            {
                var snapshot = _textView?.TextSnapshot;
                if (snapshot == null)
                    return _textView.GetWordBeforeCaretStart();

                int start = Math.Min(Math.Max(caretPos, 0), snapshot.Length);
                while (start > 0 && IsObjectReferencePrefixChar(snapshot[start - 1]))
                {
                    start--;
                }

                return start;
            }
            catch (Exception ex)
            {
                Logger.Warn($"GetObjectReferenceBeforeCaretStart failed: {ex.Message}");
                return _textView.GetWordBeforeCaretStart();
            }
        }

        private int GetJoinSuggestionReplaceEnd(int caretPos)
        {
            try
            {
                var snapshot = _textView?.TextSnapshot;
                if (snapshot == null)
                    return caretPos;

                string text = snapshot.GetText();
                int end = Math.Min(Math.Max(caretPos, 0), snapshot.Length);

                // If the caret is in the middle of a partially typed table token,
                // replace the whole token, not just the left side.
                while (end < snapshot.Length && IsObjectReferencePrefixChar(text[end]))
                    end++;

                int lineEnd = end;
                while (lineEnd < snapshot.Length && text[lineEnd] != '\r' && text[lineEnd] != '\n')
                    lineEnd++;

                if (lineEnd <= end)
                    return end;

                var tail = text.Substring(end, lineEnd - end);
                var onMatch = Regex.Match(tail, @"^\s+ON\b", RegexOptions.IgnoreCase);
                if (!onMatch.Success)
                    return end;

                // Replace a stale/partial ON clause after the caret with the
                // FK-generated ON clause. Stop before the next clause on the same line.
                var boundary = JoinSuggestionTailBoundaryPattern.Match(tail, onMatch.Index + onMatch.Length);
                return boundary.Success ? end + boundary.Index : lineEnd;
            }
            catch (Exception ex)
            {
                Logger.Warn($"GetJoinSuggestionReplaceEnd failed: {ex.Message}");
                return caretPos;
            }
        }

        private static bool IsObjectReferenceCompletion(CompletionItemData item)
        {
            return item != null &&
                (item.Kind == CompletionItemKind.Table ||
                 item.Kind == CompletionItemKind.View ||
                 item.Kind == CompletionItemKind.Database ||
                 item.Kind == CompletionItemKind.JoinSuggestion ||
                 ((item.Kind == CompletionItemKind.Procedure ||
                   item.Kind == CompletionItemKind.Function) &&
                  !string.IsNullOrEmpty(item.InsertText) &&
                  item.InsertText.StartsWith("[", StringComparison.Ordinal)));
        }

        private static bool IsObjectReferencePrefixChar(char c)
        {
            return char.IsLetterOrDigit(c) || c == '_' || c == '.' || c == '[' || c == ']';
        }

        /// <summary>
        /// Returns true when the last non-whitespace character before the caret is a standalone
        /// <c>*</c> at paren-depth 0 inside a SELECT clause (e.g. <c>SELECT *</c>).
        /// Returns false for aggregate wildcards such as <c>COUNT(*)</c>.
        /// </summary>
        private static bool IsAfterStarInSelect(string text, int caretOffset)
        {
            if (string.IsNullOrEmpty(text) || caretOffset <= 0)
                return false;

            int pos = caretOffset - 1;
            while (pos >= 0 && char.IsWhiteSpace(text[pos]))
                pos--;

            if (pos < 0 || text[pos] != '*')
                return false;

            // Reject * inside parentheses — it's an aggregate wildcard (COUNT(*), etc.)
            int depth = 0;
            for (int i = 0; i < pos; i++)
            {
                if (text[i] == '(') depth++;
                else if (text[i] == ')' && depth > 0) depth--;
            }
            if (depth > 0)
                return false;

            return SqlContextParser.GetContext(text, pos) == SqlContext.SelectClause;
        }

        /// <summary>
        /// Shows a one-item hint popup informing the user that Tab will expand * to all columns.
        /// </summary>
        private void ShowStarExpansionHint()
        {
            var hint = new CompletionItemData
            {
                Text = "Expand * to all columns",
                Description = "Press Tab to replace * with the full alias.column list",
                Kind = CompletionItemKind.Status,
                Priority = 0
            };
            _popup.Show(_textView, new System.Collections.Generic.List<CompletionItemData> { hint });
        }

        /// <summary>
        /// Replaces the <c>*</c> before the caret with a comma-separated list of
        /// alias.column items derived from the current query's FROM/JOIN tables.
        /// Returns true if the expansion was performed, false if schema is unavailable
        /// or no tables could be resolved.
        /// </summary>
        private bool TryExpandStarToColumns(string fullText, int caretOffset)
        {
            try
            {
                int starPos = caretOffset - 1;
                while (starPos >= 0 && char.IsWhiteSpace(fullText[starPos]))
                    starPos--;

                if (starPos < 0 || fullText[starPos] != '*')
                    return false;

                string server = null, database = null, connectionString = null;
                var connInfo = ConnectionTracker.GetActiveConnection();
                if (connInfo != null)
                {
                    server = connInfo.Server;
                    database = connInfo.Database;
                    connectionString = connInfo.ConnectionString;
                }

                var schema = SchemaCache.GetOrLoad(server, database, connectionString);
                if (schema == null || !schema.IsLoaded)
                    return false;

                string expanded = CompletionEngine.GetStarExpansion(fullText, schema);
                if (string.IsNullOrEmpty(expanded))
                    return false;

                _textView.ReplaceSpan(starPos, 1, expanded);
                Logger.Diagnostic($"Expanded SELECT * to {expanded.Split(',').Length} column(s)");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error("TryExpandStarToColumns failed", ex);
                return false;
            }
        }

        /// <summary>
        /// Extracts the typed character from the pvaIn marshal pointer.
        /// </summary>
        private static char GetTypedChar(IntPtr pvaIn)
        {
            try
            {
                if (pvaIn == IntPtr.Zero)
                    return '\0';

                return (char)(ushort)Marshal.GetObjectForNativeVariant(pvaIn);
            }
            catch
            {
                return '\0';
            }
        }

        /// <summary>
        /// Safely checks if the popup is currently visible.
        /// </summary>
        private bool IsPopupVisible()
        {
            try
            {
                return _popup != null && _popup.IsVisible;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Safely dismisses the completion popup.
        /// </summary>
        private void DismissPopup()
        {
            try
            {
                StopNativeCompletionSuppression();
                _popup?.Dismiss();
            }
            catch (Exception ex)
            {
                Logger.Error("DismissPopup failed", ex);
            }
        }

        private void DismissNativeCompletionPopup()
        {
            DismissNativeCompletionSessions();
            SendNativeCancelCommand();
        }

        private void DismissNativeCompletionSessions()
        {
            try
            {
                if (_nativeCompletionBroker == null || _textView == null)
                    return;

                var sessions = _nativeCompletionBroker.GetSessions(_textView);
                foreach (var session in sessions)
                {
                    try
                    {
                        session.Dismiss();
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"Could not dismiss native completion session: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"Could not query native completion sessions: {ex.Message}");
            }
        }

        private void SendNativeCancelCommand()
        {
            try
            {
                if (NextTarget == null)
                    return;

                var cmdGroup = VSConstants.VSStd2K;
                uint cmdId = (uint)VSConstants.VSStd2KCmdID.CANCEL;
                NextTarget.Exec(ref cmdGroup, cmdId, 0, IntPtr.Zero, IntPtr.Zero);
            }
            catch (Exception ex)
            {
                Logger.Warn($"Could not send native completion cancel command: {ex.Message}");
            }
        }

        private void StartNativeCompletionSuppression()
        {
            try
            {
                var dispatcher = _textView?.VisualElement?.Dispatcher;
                if (dispatcher == null)
                    return;

                _nativeCompletionSuppressUntilUtc = DateTime.UtcNow.AddMilliseconds(NativeCompletionSuppressDurationMs);

                DismissNativeCompletionPopup();

                if (_nativeCompletionSuppressTimer == null)
                {
                    _nativeCompletionSuppressTimer = new DispatcherTimer(
                        DispatcherPriority.Background,
                        dispatcher)
                    {
                        Interval = TimeSpan.FromMilliseconds(125)
                    };
                    _nativeCompletionSuppressTimer.Tick += OnNativeCompletionSuppressTimerTick;
                }

                if (!_nativeCompletionSuppressTimer.IsEnabled)
                    _nativeCompletionSuppressTimer.Start();
            }
            catch (Exception ex)
            {
                Logger.Warn($"Could not start native completion suppression: {ex.Message}");
            }
        }

        private void OnNativeCompletionSuppressTimerTick(object sender, EventArgs e)
        {
            try
            {
                if (_isDisposed ||
                    !IsPopupVisible() ||
                    DateTime.UtcNow >= _nativeCompletionSuppressUntilUtc)
                {
                    StopNativeCompletionSuppression();
                    return;
                }

                DismissNativeCompletionPopup();
            }
            catch (Exception ex)
            {
                Logger.Warn($"Native completion suppression tick failed: {ex.Message}");
                StopNativeCompletionSuppression();
            }
        }

        private void StopNativeCompletionSuppression()
        {
            try
            {
                if (_nativeCompletionSuppressTimer != null)
                    _nativeCompletionSuppressTimer.Stop();
            }
            catch (Exception ex)
            {
                Logger.Warn($"Could not stop native completion suppression: {ex.Message}");
            }
        }

        /// <summary>
        /// Passes the command to the next target in the command chain.
        /// </summary>
        private int PassToNextTarget(ref Guid pguidCmdGroup, uint nCmdID, uint nCmdexecopt, IntPtr pvaIn, IntPtr pvaOut)
        {
            try
            {
                if (NextTarget != null)
                    return NextTarget.Exec(ref pguidCmdGroup, nCmdID, nCmdexecopt, pvaIn, pvaOut);
            }
            catch (Exception ex)
            {
                Logger.Error("PassToNextTarget failed", ex);
            }

            return VSConstants.S_OK;
        }

        /// <summary>
        /// Handles editor losing focus — dismiss popup.
        /// </summary>
        private void OnEditorLostFocus(object sender, EventArgs e)
        {
            try
            {
                DismissPopup();
            }
            catch (Exception ex)
            {
                Logger.Error("OnEditorLostFocus failed", ex);
            }
        }

        /// <summary>
        /// Handles editor being closed — clean up resources.
        /// </summary>
        private void OnEditorClosed(object sender, EventArgs e)
        {
            try
            {
                if (_isDisposed)
                    return;

                _isDisposed = true;

                DismissPopup();
                if (_nativeCompletionSuppressTimer != null)
                {
                    _nativeCompletionSuppressTimer.Tick -= OnNativeCompletionSuppressTimerTick;
                    _nativeCompletionSuppressTimer = null;
                }

                SchemaCache.OnSchemaLoaded -= OnSchemaReady;
                SchemaCache.OnSchemaLoadFailed -= OnSchemaLoadFailed;
                DatabaseListCache.OnDatabasesLoaded -= OnDatabaseListReady;
                DatabaseListCache.OnDatabaseListLoadFailed -= OnDatabaseListLoadFailed;
                ProcedureCache.OnProceduresLoaded -= OnProcedureListReady;
                ProcedureCache.OnProcedureLoadFailed -= OnProcedureListLoadFailed;

                if (_popup != null)
                {
                    _popup.ItemAccepted -= OnItemAccepted;
                    _popup = null;
                }

                if (_textView != null)
                {
                    _textView.LostAggregateFocus -= OnEditorLostFocus;
                    _textView.Closed -= OnEditorClosed;
                }

                Logger.Log("CompletionCommandFilter cleaned up.");
            }
            catch (Exception ex)
            {
                Logger.Error("OnEditorClosed cleanup failed", ex);
            }
        }
    }
}
