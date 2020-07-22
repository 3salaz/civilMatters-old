/*
Attach this script to a RectTransform, probably the content of a scroll area,
to make it a MiniScript code editor.
*/

using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using TMPro;
using System.Text.RegularExpressions;
using UnityEditor;

namespace Miniscript {
	
public class CodeEditor : Selectable, IDragHandler {
	
	#region Public Properties
	
	public struct TextPosition {
		public int line;			// 0-based index into lines
		public int offset;			// how many characters on that line are to the left of this position
		
		public TextPosition(int line, int offset) {
			this.line = line;
			this.offset = offset;
		}
		
		public override string ToString() {
			return string.Format("{0}:{1}", line, offset);
		}
	}

	[Tooltip("Text or TextMeshProUGUI serving as the prototype for each source code line")]
	public Graphic sourceCodeLinePrototype;
	
	[Tooltip("Optional Text or TextMeshProUGUI prototype for a line number")]
	public Graphic lineNumberPrototype;
	
	[Tooltip("What number line numbers should start at")]
	public int lineNumbersStartAt = 1;
	
	[Tooltip("What format string should be used for line numbers")]
	public string lineNumberFormatString = "00000";
	
	[Tooltip("Insertion-point (probably blinking) cursor")]
	public Graphic caret;
	
	[Tooltip("Image used (and cloned as needed) to show the selection highlight")]
	public Graphic selectionHighlight;
	
	[Tooltip("Style to apply; if left null, will look on the parent for a CodeStyling component")]
	public CodeStyling style;
	
	[Tooltip("How long to wait before starting to generate repeat inputs when a key is held")]
	public float keyRepeatDelay = 0.3f;
	
	[Tooltip("Interval between repeat inputs when a key is held")]
	public float keyRepeatInterval = 0.075f;
	
	[Tooltip("Max seconds between mouse-up and mouse-down to be considered a double-click")]
	public float doubleClickTime = 0.3f;
	
	[Tooltip("How many levels of undo/redo to support")]
	public int undoLimit = 20;
	
	public TextAsset initialSourceCode;
	
	public string source {
		get {
			var sb = new System.Text.StringBuilder();
			foreach (string line in sourceLines) {
				sb.Append(line);
				sb.Append("\n");
			}
			return sb.ToString();
		}
		set {
			LoadSource(value);
		}
	}
	
	public string[] sourceArray {
		get { return sourceLines.ToArray(); }
	}
	
	public bool canUndo {
		get { return undoPosition >= 0; }	
	}
	public bool canRedo {
		get { return undoPosition+2 < undoStack.Count; }
	}
	public bool isFocused {
		get { return hasFocus; }
	}
	
	#endregion
	#region Private Properties
	
	
	[System.Serializable]
	public struct UndoState {
		public string source;
		public TextPosition selAnchor;
		public TextPosition selEndpoint;
	}
	
	List<string> sourceLines;		// our current source, split into lines
	List<Graphic> uiTexts;			// Text or TMProUGUI that draw our source lines
	List<Graphic> lineNums;			// Text or TMProUGUI that draw our line numbers
	Graphic selHighlightMid;		// highlight used for the middle lines of a multi-line selection
	Graphic selHighlight2;			// selection highlight used at the end of a multi-line selection
	TextPosition selAnchor;			// caret position, or start of an extended selection
	TextPosition selEndpoint;		// "active" end of an extended selection, if different from anchor
	float preferredX;				// caret X position within the line which we will set on up/down if possible
	Dictionary<KeyCode, float> keyRepeatTime;	// Time.time at which a given key can repeat
	float caretOnTime;				// Time.time at which caret started the "on" phase of its blink
	List<UndoState> undoStack;		// buffer of undo/redo states
	int undoPosition;				// index of next item to undo in UndoStack
	float lastEditTime;				// Time.time of last edit (so we know when to combine with the last undo state)
	ScrollRect scrollRect;			// the scroll view we are the content of (if any)
	RectTransform scrollMask;		// viewport mask
	float mouseUpTime;				// Time.time at which mouse last went up
	int clickCount;					// 1 for single click, 2 for double-click, 3 for triple-click
	bool hasFocus;					// true when we have the keyboard focus
	
	bool extendedSelection { get { return selEndpoint.line != selAnchor.line || selEndpoint.offset != selAnchor.offset; } }
	
	TextPosition selEndFirst {
		get {
			if (selAnchor.line < selEndpoint.line 
					|| (selAnchor.line == selEndpoint.line && selAnchor.offset < selEndpoint.offset)) {
				return selAnchor;
			}
			return selEndpoint;
		}
	}
	TextPosition selEndLast {
		get {
			if (selAnchor.line > selEndpoint.line 
				|| (selAnchor.line == selEndpoint.line && selAnchor.offset > selEndpoint.offset)) {
				return selAnchor;
				}
			return selEndpoint;
		}
	}
	
	#endregion
	#region Unity Interface Methods
	
	protected override void Awake() {
		base.Awake();
		undoStack = new List<UndoState>();
		undoPosition = -1;
		keyRepeatTime = new Dictionary<KeyCode, float>();
		if (style == null) style = GetComponentInParent<CodeStyling>();
		scrollRect = GetComponentInParent<ScrollRect>();
		Mask m = GetComponentInParent<Mask>();
		if (m != null) scrollMask = m.rectTransform;

		selHighlightMid = Instantiate(selectionHighlight, transform);
		selHighlight2 = Instantiate(selectionHighlight, transform);
		selHighlightMid.transform.SetAsFirstSibling();
		selHighlight2.transform.SetAsFirstSibling();

		lastEditTime = -1;
		selAnchor = selEndpoint = new TextPosition(0, 0);
	}
	
	protected override void Start() {
		if (!Application.isPlaying) return;	// don't run this stuff in the editor!
		base.Start();
		
		if (sourceLines == null) {
			if (initialSourceCode != null) LoadSource(initialSourceCode.text);
			else LoadSource("\n");
		}
		
		UpdateSelectionDisplay();
		
		caret.gameObject.SetActive(false);
		Select();
	}
	
	protected void Update() {
		if (!hasFocus) return;
		
		ProcessKeys();
		
		if (!extendedSelection) {
			// blink the caret
			if (Time.time > caretOnTime + 0.7) {
				caret.gameObject.SetActive(false);
				if (Time.time > caretOnTime + 1f) CaretOn();
			}
		}
	}
	
	protected void OnGUI() {
		if (!hasFocus) return;
		Event e = Event.current;
		#if UNITY_STANDALONE_LINUX && !UNITY_EDITOR
		if (e.isKey && e.type == EventType.KeyDown && e.character > 0) {
        #else
		if (e.isKey && e.keyCode == KeyCode.None) {
        #endif
			char c = e.character;
			if (c == '\n' && e.shift) {
				SetSelText("\n");
				string ender = FindDefaultEnder();
				if (ender != null) {
					var pos = selAnchor;
					SetSelText("\n" + ender);
					selAnchor = selEndpoint = pos;
					UpdateSelectionDisplay();
				}
			} else {
				//Debug.Log("Current state: " + sourceLines.Count + " lines, selAnchor=" + selAnchor);
				//Debug.Log("Setting selText text to " + c + "  (" + (int)c + ")");
				SetSelText(c.ToString());
			}
			ScrollIntoView(selEndpoint);
		}
	}
	
	public override void OnSelect(BaseEventData eventData) {
		//Debug.Log(gameObject.name + " now has the focus");
		base.OnSelect(eventData);
		hasFocus = true;
	}
	
	public override void OnDeselect(BaseEventData eventData) {
		//Debug.Log(gameObject.name + " lost focus");
		base.OnDeselect(eventData);
		hasFocus = false;
		caret.gameObject.SetActive(false);
	}
	
	public override void OnPointerDown(PointerEventData eventData) {
		base.OnPointerDown(eventData);
		float upTime = Time.time - mouseUpTime;
		if (upTime > doubleClickTime) clickCount = 1;
		else clickCount++;
		
		Vector2 pos;
		RectTransformUtility.ScreenPointToLocalPointInRectangle(transform as RectTransform, 
			eventData.position, eventData.pressEventCamera, out pos);
		selEndpoint = PositionAtXY(pos);
		bool forward = (selEndpoint.line > selAnchor.line 
			|| (selEndpoint.line == selAnchor.line && selEndpoint.offset > selAnchor.offset));
		if (!Input.GetKey(KeyCode.LeftShift) && !Input.GetKey(KeyCode.RightShift)) {
			selAnchor = selEndpoint;
		}
		ExtendSelection(forward);
		UpdateSelectionDisplay();
		preferredX = caret.rectTransform.anchoredPosition.x;
		
		eventData.Use();
	}
	
	public override void OnPointerUp(PointerEventData eventData) {
		mouseUpTime = Time.time;
		
		eventData.Use();
	}
	

	public void OnDrag(PointerEventData eventData) {
		Vector2 pos;
		RectTransformUtility.ScreenPointToLocalPointInRectangle(transform as RectTransform, 
			eventData.position, eventData.pressEventCamera, out pos);
		selEndpoint = PositionAtXY(pos);
		bool forward = (selEndpoint.line > selAnchor.line 
			|| (selEndpoint.line == selAnchor.line && selEndpoint.offset > selAnchor.offset));
		ExtendSelection(forward);
		UpdateSelectionDisplay();
		eventData.Use();
	}
	
	#endregion
	#region Public Methods
	
	[ContextMenu("Edit Cut")]
	public void EditCut() {
		GUIUtility.systemCopyBuffer = SelectedText();
		SetSelText("");
	}
	
	[ContextMenu("Edit Copy")]
	public void EditCopy() {
		GUIUtility.systemCopyBuffer = SelectedText();
	}
	
	[ContextMenu("Edit Paste")]
	public void EditPaste() {
		SetSelText(GUIUtility.systemCopyBuffer);
		ScrollIntoView(selEndpoint);
	}
	
	[ContextMenu("Edit Delete")]
	public void EditDelete() {
		SetSelText("");
		ScrollIntoView(selEndpoint);
	}
	
	[ContextMenu("Edit Select All")]
	public void EditSelectAll() {
		selAnchor = new TextPosition(0, 0);
		selEndpoint = new TextPosition(sourceLines.Count-1, sourceLines[sourceLines.Count-1].Length);
		UpdateSelectionDisplay();
	}
	
	[ContextMenu("Edit Undo")]
	public void EditUndo() {
		if (undoPosition >= 0) {
			if (undoPosition == undoStack.Count-1) {
				// At the top of the stack; add another entry to redo to where we currently are.
				undoStack.Add(GetUndoState());
			}
			ApplyUndo(undoStack[undoPosition]);
			undoPosition--;
		}
		ScrollIntoView(selEndpoint);
	}
	
	[ContextMenu("Edit Redo")]
	public void EditRedo() {
		if (undoPosition+2 < undoStack.Count) {
			ApplyUndo(undoStack[undoPosition+2]);
			undoPosition++;
		}
		ScrollIntoView(selEndpoint);
	}
	
	public void Indent(int levels) {
		TextPosition startPos = selEndFirst;
		TextPosition endPos = selEndLast;
		for (int i=startPos.line; i<=endPos.line; i++) {
			string s = sourceLines[i];
			if (levels > 0) s = "\t\t\t\t\t\t\t\t\t\t".Substring(0, levels) + s;
			else {
				for (int j=0; j<-levels; j++) {
					if (s[0] == '\t' || s[0] == ' ') s = s.Substring(1);
				}
			}
			UpdateLine(i, s);
		}
		selAnchor = new TextPosition(startPos.line, 0);
		selEndpoint = new TextPosition(endPos.line, sourceLines[endPos.line].Length);
		UpdateSelectionDisplay();
	}
	
	public void ScrollIntoView(TextPosition position) {
		float lineHeight = sourceCodeLinePrototype.rectTransform.sizeDelta.y;
		Vector2 targetPosition = new Vector2(InsertionPointX(position.line, position.offset),
			lineHeight * position.line);
		
		//Canvas.ForceUpdateCanvases();
		
		var contentPanel = (transform as RectTransform);
		//contentPanel.anchoredPosition =
		//	  (Vector2)scrollRect.transform.InverseTransformPoint(contentPanel.position)
		//	- (Vector2)scrollRect.transform.InverseTransformPoint(targetPosition);

		RectTransform scrollRT = scrollRect.transform as RectTransform;
		Vector2 viewSize = scrollRT.rect.size;
		if (scrollMask != null) viewSize = scrollMask.rect.size;
		var curScroll = contentPanel.anchoredPosition;
		curScroll.x *= -1;	// (X scrolling is backwards)
		var contentSize = contentPanel.sizeDelta;
		
		if (targetPosition.y < curScroll.y) {
			// out of view above
			curScroll.y = targetPosition.y;
		} else if (targetPosition.y + lineHeight > curScroll.y + viewSize.y) {
			// out of view below
			curScroll.y = Mathf.Min(targetPosition.y + lineHeight - viewSize.y, contentSize.y - viewSize.y);
		}
		
		if (targetPosition.x < curScroll.x) {
			// out of view to the left
			curScroll.x = Mathf.Max(targetPosition.x - 20, 0);
		} else if (targetPosition.x > curScroll.x + viewSize.x) {
			// out of view to the right
			curScroll.x = Mathf.Min(targetPosition.x - viewSize.x + 50, contentSize.x - viewSize.x);
		}
		
		curScroll.x *= -1;	// (X scrolling is backwards)
		contentPanel.anchoredPosition = curScroll;
	}
	
	public string SelectedText() {
		TextPosition startPos = selEndFirst;
		TextPosition endPos = selEndLast;
		if (startPos.line == endPos.line) {
			return sourceLines[startPos.line].Substring(startPos.offset, endPos.offset - startPos.offset);
		} else {
			string result = sourceLines[startPos.line].Substring(startPos.offset);
			for (int i=startPos.line+1; i<endPos.line; i++) result += "\n" + sourceLines[i];
			result += "\n" + sourceLines[endPos.line].Substring(0, endPos.offset);
			return result;
		}
	}
	
	#endregion
	
	#region Private Methods
	
	/// <summary>
	/// Move selEndpoint by one character in either direction.
	/// </summary>
	/// <param name="dChar">+1 to advance to the right; -1 to retreat to the left</param>
	/// <returns>true if moved, false if hit a limit</returns>
	bool AdvanceOne(int dChar) {
		selEndpoint.offset += dChar;
		if (selEndpoint.offset < 0) {
			if (selEndpoint.line == 0) {
				selEndpoint.offset = 0;
				return false;
			} else {
				selEndpoint.line--;
				selEndpoint.offset = sourceLines[selEndpoint.line].Length;
			}
		} else if (selEndpoint.offset > sourceLines[selEndpoint.line].Length) {
			if (selEndpoint.line == sourceLines.Count - 1) {
				selEndpoint.offset = sourceLines[selEndpoint.line].Length;
				return false;
			} else {
				selEndpoint.line++;
				selEndpoint.offset = 0;
			}
		}
		return true;
	}
	
	void AdjustContentSize() {
		Vector2 size = new Vector2();
		float lineHeight = sourceCodeLinePrototype.rectTransform.sizeDelta.y;
		size.y = lineHeight * (sourceLines.Count + 1);
		
		size.x = (transform.parent as RectTransform).rect.width;
		foreach (var uiText in uiTexts) {
			size.x = Mathf.Max(size.x, uiText.rectTransform.sizeDelta.x + 20);
		}
		(transform as RectTransform).sizeDelta = size;
	}
	
	void ApplyUndo(UndoState state) {
		source = state.source;
		selAnchor = state.selAnchor;
		selEndpoint = state.selEndpoint;
		UpdateSelectionDisplay();
	}
	
	void CaretOn() {
		if (!extendedSelection) {
			caret.gameObject.SetActive(true);
			caretOnTime = Time.time;
		}
	}
	
	char CharAtPosition(TextPosition pos) {
		if (pos.offset >= sourceLines[pos.line].Length) return '\n';
		return sourceLines[pos.line][pos.offset];
	}
	
	void DeleteBack() {
		if (extendedSelection) SetSelText("");
		else if (selAnchor.line > 0 || selAnchor.offset > 0) {
			var save = selAnchor;
			// Special case: if we're at the start of the correct indentation for this line,
			// then delete the whole indentation and line break.  Otherwise, delete just
			// the character to the left (i.e. the standard case).
			if (selAnchor.offset <= Indentation(sourceLines[selAnchor.line])) {
				MoveCursor(-1, 0, true);	// go to start of line
				MoveCursor(-1, 0);			// go one more
			} else {
				MoveCursor(-1, 0);
			}
			selAnchor = save;
			SetSelText("");
		}
	}
	
	void DeleteForward() {
		if (extendedSelection) SetSelText("");
		else {
			var save = selAnchor;
			MoveCursor(1, 0);
			//Debug.Log("DeleteForward moved cursor from " + save.offset + " to " + selAnchor.offset);
			selAnchor = save;
			//Debug.Log("Now extendedSelection=" + extendedSelection + ", from " + selAnchor.offset + " to " + selEndpoint.offset);
			SetSelText("");
		}
	}
	
	void DeleteLine(int lineNum) {
		sourceLines.RemoveAt(lineNum);
		Destroy(uiTexts[lineNum].gameObject);
		uiTexts.RemoveAt(lineNum);
	}
	
	void ExtendSelection(bool forward) {
		if (clickCount == 2) {
			// Extend selection to whole word (or quoted/parenthesized bit)
			if (!extendedSelection) {
				string line = sourceLines[selAnchor.line];
				// Check for double-click on quotation mark, paren, or bracket; move anchor to other end
				int pos = -1;
				if (selAnchor.offset > 0 && "()[]\"".IndexOf(line[selAnchor.offset-1]) >= 0) {
					pos = FindMatchingToken(line, selAnchor.offset-1);
				} else if (selAnchor.offset < line.Length && selAnchor.offset > 0 && "()[]\"".IndexOf(line[selAnchor.offset-1]) >= 0) {
					pos = FindMatchingToken(line, selAnchor.offset);
				} else {
					// Didn't hit a quotation mark, paren, or bracket, so extend to whole word
					selAnchor.offset = FindWordStart(line, selAnchor.offset);
					selEndpoint.offset = FindWordEnd(line, selEndpoint.offset);
				}
				if (pos >= 0) {
					selAnchor.offset = pos;
					if (selAnchor.offset > selEndpoint.offset) {
						// extend to include the parens/brackets/quotes, too
						selAnchor.offset++;
						selEndpoint.offset--;
					}
				}
			} else {
				// Once we already have an extended selection, then just grow it
				// by words (don't try to get clever with quotes etc.).
				if (forward) {
					selAnchor.offset = FindWordStart(sourceLines[selAnchor.line], selAnchor.offset);
					selEndpoint.offset = FindWordEnd(sourceLines[selEndpoint.line], selEndpoint.offset);
				} else {
					selAnchor.offset = FindWordEnd(sourceLines[selAnchor.line], selAnchor.offset);
					selEndpoint.offset = FindWordStart(sourceLines[selEndpoint.line], selEndpoint.offset);				
				}
			}
		} else if (clickCount > 2) {
			// Extend selection to whole lines
			if (forward) {
				selAnchor.offset = 0;
				selEndpoint.offset = sourceLines[selEndpoint.line].Length;
			} else {
				selAnchor.offset = sourceLines[selAnchor.line].Length;
				selEndpoint.offset = 0;
			}
		}
	}
	
	/// <summary>
	/// Find the opening/closing token that matches the quotation
	/// mark, parenthesis, or square-bracket at the given position.
	/// </summary>
	/// <param name="line">source code line of interest</param>
	/// <param name="pos">position of a token to match</param>
	/// <returns>position of matching token, or -1 if not found</returns>
	int FindMatchingToken(string line, int pos) {
		char tok = line[pos];
		if (tok == '(' || tok == '[') {
			char closeTok = (tok == '(' ? ')' : ']');
			int depth = 1;
			while (pos+1 < line.Length) {
				pos++;
				if (line[pos] == tok) depth++;
				else if (line[pos] == closeTok) {
					depth--;
					Debug.Log("found " + closeTok + " at " + pos + "; depth now " + depth);
					if (depth == 0) return pos;
				}
			}
		} else if (tok == ')' || tok == ']') {
			char openTok = (tok == ')' ? '(' : '[');
			int depth = 1;
			while (pos > 0) {
				pos--;
				if (line[pos] == tok) depth++;
				else if (line[pos] == openTok) {
					depth--;
					Debug.Log("found " + openTok + " at " + pos + "; depth now " + depth);
					if (depth == 0) return pos;
				}
			}
		} else if (tok == '"') {
			// Quotes are different.  We can't tell openers from closers without
			// simply counting the start of the string.
			bool quoteOpen = false;
			int lastQuote = -1;
			for (int i=0; i<line.Length; i++) {
				if (line[i] == '"') {
					if (i+1 < line.Length && line[i+1] == '"') {
						// double-double quote... ignore
						i++;
						continue;
					}
					if (i == pos && quoteOpen) return lastQuote;
					if (i > pos && quoteOpen) return i;
					quoteOpen = !quoteOpen;
					lastQuote = i;
				}
			}
		}
		return -1;
	}
	
	/// <summary>
	/// Find the start of the word at or before the given position.
	/// </summary>
	int FindWordStart(string line, int pos) {
		while (pos > 0) {
			char c = line[pos-1];
			if (!Lexer.IsIdentifier(c)) break;
			pos--;
		}
		return pos;
	}
	
	/// <summary>
	/// Find the end of the word at or before the given position.
	/// </summary>
	int FindWordEnd(string line, int pos) {
		while (pos < line.Length) {
			char c = line[pos];
			if (!Lexer.IsIdentifier(c)) break;
			pos++;
		}
		return pos;
	}
	
	UndoState GetUndoState() {
		var undo = new UndoState();
		undo.source = source;
		undo.selAnchor = selAnchor;
		undo.selEndpoint = selEndpoint;
		return undo;
	}
	
	/// <summary>
	/// Return the position between two characters on the given line.
	/// </summary>
	/// <param name="uiText">Text or TextMeshProUGUI</param>
	/// <param name="charOffset">how many characters to the left of the insertion point</param>
	/// <returns>X position between the indicated characters</returns>
	float InsertionPointX(int lineNum, int charOffset) {
		Graphic uiText = uiTexts[lineNum];
		string text = sourceLines[lineNum];
		float x = uiText.rectTransform.anchoredPosition.x;
		if (uiText is Text) {
			Text textObj = (Text)uiText;
			x += WidthInFont(text.Substring(0, charOffset), textObj.font, textObj.fontSize);
		} else if (uiText is TextMeshProUGUI) {
			float x0 = 0;
			var tmPro = (TextMeshProUGUI)uiText;
			TMP_TextInfo textInfo = tmPro.textInfo;
			TMP_CharacterInfo[] charInfo = textInfo.characterInfo;
			int textLen = textInfo.characterCount;
			if (charOffset > textLen) charOffset = textLen;
			if (charOffset > 0) x0 = charInfo[charOffset-1].bottomRight.x;
			float x1 = 0;
			if (charOffset >= 0 && charOffset < textLen) x1 = charInfo[charOffset].bottomLeft.x;
			if (x1 == 0) x += x0;
			else x += (x0+x1)/2;
		} else Debug.Assert(false, "UI text object must be either Text or TextMeshProUGUI");
		return x;
	}
	
	/// <summary>
	/// Count how many tabs are at the beginning of the given source line.
	/// </summary>
	int Indentation(string sourceLine) {
		for (int i=0; i<sourceLine.Length; i++) {
			if (sourceLine[i] != '\t') return i;
		}
		// Found nothing at all but tabs?  Still counts as indented.
		return sourceLine.Length;
	}
	
	/// <summary>
	/// Figure out how the given source line affects indentation.
	/// </summary>
	/// <param name="sourceLine">line of code</param>
	/// <param name="outdentThis">0, or 1 if this line should outdent relative to the previous line</param>
	/// <param name="indentNext">how much the next line should be indented relative to this one</param>
	void IndentEffect(string sourceLine, out int outdentThis, out int indentNext) {
		indentNext = outdentThis = 0;
		var lexer = new Lexer(sourceLine);
		while (!lexer.AtEnd) {
			try {
				Token tok = lexer.Dequeue();
				if (tok.type == Token.Type.Keyword) {
					if (tok.text == "if") {
						// Tricky case, because of single-line 'if'.
						// We can recognize that by having more non-comment tokens
						// right after the 'then' keyword.
						while (!lexer.AtEnd && lexer.Peek().type != Token.Type.EOL) {
							tok = lexer.Dequeue();
							if (tok.type == Token.Type.Keyword && tok.text == "then") {
								// OK, we got a "then", so if the next token is EOL, then
								// we need to indent.  If it's anything else, we don't.
								if (lexer.Peek().type == Token.Type.EOL) indentNext++;
								break;
							}
						}
					} else if (tok.text == "else" || tok.text == "else if") {
						outdentThis++;
						indentNext++;
					} else if (tok.text == "while" || tok.text == "for" || tok.text == "function") {
						indentNext++;
					} else if (tok.text.StartsWith("end")) {
						if (indentNext > 0) indentNext--;
						else outdentThis++;
					}
				}
			} catch (LexerException) {
			}
		}
	}

	/// <summary>
	/// Find the default "end" keyword to match the most recent block
	/// opener before the selection point.  This is used for the shift-Return
	/// function that automatically inserts the appropriate closer.
	/// </summary>
	/// <returns>end string (e.g. "end while"), or null</returns>
	string FindDefaultEnder() {
		int line = selAnchor.line - 1;
		var closers = new Queue<string>();
		while (line >= 0) {
			Lexer lexer = new Lexer(sourceLines[line]);
			string ender = null;
			bool onIfStatement = false;
			while (!lexer.AtEnd) {
				try {
					var tok = lexer.Dequeue();
					if (tok.type != Token.Type.Keyword) continue;
					if (tok.text.StartsWith("end ")) {
						closers.Enqueue(tok.text);
					} else if (tok.text == "while") {
						onIfStatement = false;
						if (closers.Count > 0 && closers.Peek() == "end while") {
							closers.Dequeue();
						} else {
							return "end while";
						}
					} else if (tok.text == "for") {
						onIfStatement = false;
						if (closers.Count > 0 && closers.Peek() == "end for") {
							closers.Dequeue();
						} else {
							return "end for";
						}
					} else if (tok.text == "function") {
						onIfStatement = false;
						if (closers.Count > 0 && closers.Peek() == "end function") {
							closers.Dequeue();
						} else {
							return "end function";
						}
					} else if (tok.text == "if") {
						onIfStatement = true;
					} else if (tok.text == "then" && onIfStatement) {
						// If there is any token after `then` besides EOL,
						// then this is a single-line 'if' and doesn't need a closer.
						// But if it's followed by EOL, then it's a block `if`.
						if (lexer.Peek().type == Token.Type.EOL) {
							if (closers.Count > 0 && closers.Peek() == "end if") {
								closers.Dequeue();
							} else {
								return "end if";
							}				
						}
					} else if (tok.text == "else" || tok.text == "else if") {
						onIfStatement = false;
						if (closers.Count > 0 && closers.Peek() == "end if") {
							// Don't dequeue the end-if, as that still applies!
						} else {
							return "end if";
						}				
					}
				} catch (LexerException) {
					
				}
			}
			if (ender != null) return ender;
			line--;
		}
		return null;
	}

	void InsertLine(int lineNum, string lineText) {
		sourceLines.Insert(lineNum, lineText);
		
		Graphic newUIText = Instantiate(sourceCodeLinePrototype, transform) as Graphic;
		var rt = newUIText.rectTransform;
		rt.anchorMin = rt.anchorMax = new Vector2(0, 1);
		SetText(newUIText, lineText);
		uiTexts.Insert(lineNum, newUIText);
		newUIText.gameObject.SetActive(true);
	}
	
	/// <summary>
	/// Return whether the given character is a "token" character, i.e.,
	/// something we should jump to the start or end of when using alt-arrow
	/// to move "by word".  Use numeric to determine whether dot (.) is
	/// considered part of a token, and also update it when we see digits.
	/// </summary>
	static bool IsTokenChar(char c, ref bool numeric) {
		if (c == '.') return numeric;
		if (c >= '0' && c <= '9') {
			numeric = true;
			return true;
		}
		if (c <= '/') return false;
		if (c >= ':' && c <= '@') return false;
		if (c >= '{' && c <= '~') return false;
		return true;
	}
	
	bool KeyPressedOrRepeats(KeyCode keyCode) {
		if (Input.GetKeyDown(keyCode)) {
			keyRepeatTime[keyCode] = Time.time + keyRepeatDelay;
			return true;
		} else if (Input.GetKey(keyCode)) {
			if (Time.time > keyRepeatTime[keyCode]) {
				keyRepeatTime[keyCode] = Time.time + keyRepeatInterval;
				return true;
			}
		}
		return false;
	}
		
	void LoadSource(string sourceCode) {
		sourceCodeLinePrototype.gameObject.SetActive(false);
		if (uiTexts == null) uiTexts = new List<Graphic>();
		if (sourceLines == null) sourceLines = new List<string>();
		
		//sourceCode = sourceCode.ReplaceLineEndings("\n");
		int lineNum = 0;
		foreach (string lineText in sourceCode.Split(new char[]{'\n'})) {
			if (lineNum >= sourceLines.Count) {
				InsertLine(lineNum, lineText);
			} else {
				sourceLines[lineNum] = lineText;
				SetText(uiTexts[lineNum], lineText);
			}
			lineNum++;
		}
		
		while (sourceLines.Count > lineNum) {
			DeleteLine(sourceLines.Count-1);
		}
		
		UpdateLineYPositions();
		
		int maxLine = Max(sourceLines.Count - 1, 0);
		selAnchor.line = Min(selAnchor.line, maxLine);
		selAnchor.offset = Min(selAnchor.offset, selAnchor.line < sourceLines.Count ? sourceLines[selAnchor.line].Length : 0);
		selEndpoint.line = Min(selEndpoint.line, maxLine);
		selEndpoint.offset = Min(selEndpoint.offset, selEndpoint.line < sourceLines.Count ? sourceLines[selEndpoint.line].Length : 0);		
		UpdateSelectionDisplay();
	}
	
	static int Max(int a, int b) {
		return a > b ? a : b;
	}
	
	static int Min(int a, int b) {
		return a < b ? a : b;
	}
	
	/// <summary>
	/// Update the selection according to the given keyboard input.
	/// </summary>
	/// <param name="dChar">left/right key input</param>
	/// <param name="dLine">up/down key input</param>
	/// <param name="allTheWay">go to start/end of line or document</param>
	void MoveCursor(int dChar, int dLine, bool allTheWay=false) {
		bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
		if (extendedSelection && !shift) {
			if (dChar != 0) {
				// Collapse the selection towards whichever end is in the direction we're moving, and return.
				if (dChar > 0) selAnchor = selEndpoint = selEndLast;
				else selAnchor = selEndpoint = selEndFirst;
				UpdateSelectionDisplay();
				preferredX = caret.rectTransform.anchoredPosition.x;
				CaretOn();
				return;
			} else {
				// Collaspe the selection towards the indicated end, and then process the up/down key
				if (dLine > 0) selAnchor = selEndpoint = selEndLast;
				else selAnchor = selEndpoint = selEndFirst;
			}
		}
		if (dChar != 0) {
			bool byWord = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
			if (allTheWay) {
				// Go to start/end of line.
				if (dChar < 0) selEndpoint.offset = 0;
				else selEndpoint.offset = sourceLines[selEndpoint.line].Length;
			} else if (byWord) {
				// skip any nonword characters; then advance till we get to nonword chars again
				bool numeric = false;
				if (dChar < 0) AdvanceOne(-1);
				char c = CharAtPosition(selEndpoint);
				while (!IsTokenChar(c, ref numeric) && AdvanceOne(dChar)) {
					c = CharAtPosition(selEndpoint);
				}
				while (IsTokenChar(c, ref numeric) && AdvanceOne(dChar)) {
					c = CharAtPosition(selEndpoint);
				}
				if (dChar < 0) AdvanceOne(1);
			} else AdvanceOne(dChar);
		}
		if (dLine < 0) {
			if (allTheWay) {
				// Go to start of document
				selEndpoint.line = 0;
				selEndpoint.offset = 0;
			} else if (selEndpoint.line == 0) {
				// Up-arrow on first line: jump to start of line (Mac thing, but cool everywhere)
				selEndpoint.offset = 0;
			} else {
				selEndpoint.line--;
				selEndpoint.offset = OffsetForXPosition(uiTexts[selEndpoint.line], preferredX);
			}
		} else if (dLine > 0) {
			if (allTheWay) {
				// Go to end of document
				selEndpoint.line = sourceLines.Count - 1;
				selEndpoint.offset = sourceLines[selEndpoint.line].Length;
			} else if (selEndpoint.line == sourceLines.Count - 1) {
				// Down-arrow on last line: jump to end of line
				selEndpoint.offset = sourceLines[selEndpoint.line].Length;
			} else {
				selEndpoint.line++;
				selEndpoint.offset = OffsetForXPosition(uiTexts[selEndpoint.line], preferredX);
			}
		}
		if (!shift) {
			// Without shift key held, move the caret, rather than extending the selection.
			selAnchor = selEndpoint;
		}
		
		UpdateSelectionDisplay();
		if (dChar != 0) preferredX = caret.rectTransform.anchoredPosition.x;
		CaretOn();
		ScrollIntoView(selEndpoint);
	}
	
	/// <summary>
	/// Find number of characters to the left of the given X position in the given line.
	/// </summary>
	/// <param name="uiText">Text or TextMeshProUGUI</param>
	/// <param name="x">x offset relative to parent container</param>
	/// <returns>character offset within that line</returns>
	int OffsetForXPosition(Graphic uiText, float x) {
		int result = 0;
		x -= sourceCodeLinePrototype.rectTransform.anchoredPosition.x;
		if (uiText is Text) {
			Text text = (Text)uiText;
			float totalWidth = 0;
			foreach (char c in text.text.ToCharArray()) {
				CharacterInfo characterInfo;
				text.font.GetCharacterInfo(c, out characterInfo, text.fontSize);
				totalWidth += characterInfo.advance;
				if (totalWidth > x) break;
				result++;
			}			
		} else if (uiText is TextMeshProUGUI) {
			var tmPro = (TextMeshProUGUI)uiText;
			TMP_TextInfo textInfo = tmPro.textInfo;
			TMP_CharacterInfo[] charInfo = textInfo.characterInfo;
			for (int i=0; i<textInfo.characterCount; i++) {
				float midPoint = (charInfo[i].bottomLeft.x + charInfo[i].bottomRight.x) / 2;
				if (midPoint > x) break;
				result++;
			}
		}
		return result;
	}
	
	TextPosition PositionAtXY(Vector2 xy) {
		int lineNum = Mathf.FloorToInt(Mathf.Abs(xy.y) / sourceCodeLinePrototype.rectTransform.sizeDelta.y);
		if (lineNum < 0) return new TextPosition(0, 0);
		if (lineNum >= sourceLines.Count) return new TextPosition(sourceLines.Count-1, sourceLines[sourceLines.Count-1].Length);
		return new TextPosition(lineNum, Min(sourceLines[lineNum].Length, OffsetForXPosition(uiTexts[lineNum], xy.x)));
	}
	
	void ProcessKeys() {
		if (!hasFocus || !Input.anyKey) return;	// quick bail-out
		
		bool cmd = Input.GetKey(KeyCode.LeftCommand) || Input.GetKey(KeyCode.RightCommand);
		bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
		if (KeyPressedOrRepeats(KeyCode.LeftArrow)) MoveCursor(-1, 0, cmd);
		if (KeyPressedOrRepeats(KeyCode.RightArrow)) MoveCursor(1, 0, cmd);
		if (KeyPressedOrRepeats(KeyCode.UpArrow)) MoveCursor(0, -1, cmd);
		if (KeyPressedOrRepeats(KeyCode.DownArrow)) MoveCursor(0, 1, cmd);
		if (Input.GetKeyDown(KeyCode.Home)) {
			if (ctrl) MoveCursor(0, -1, true);
			else MoveCursor(-1, 0, true);
		}
		if (Input.GetKeyDown(KeyCode.End)) {
			if (ctrl) MoveCursor(0, 1, true);
			else MoveCursor(1, 0, true);
		}
		
		if (KeyPressedOrRepeats(KeyCode.Backspace)) DeleteBack();
		if (KeyPressedOrRepeats(KeyCode.Delete)) DeleteForward();
		
		if (cmd || ctrl) {
			// Process keyboard shortcuts
			if (Input.GetKeyDown(KeyCode.X)) EditCut();
			if (Input.GetKeyDown(KeyCode.C)) EditCopy();
			if (Input.GetKeyDown(KeyCode.V)) EditPaste();
			if (Input.GetKeyDown(KeyCode.A)) EditSelectAll();
			if (Input.GetKeyDown(KeyCode.LeftBracket)) Indent(-1);
			if (Input.GetKeyDown(KeyCode.RightBracket)) Indent(1);
			if (Input.GetKeyDown(KeyCode.Z)) {
				if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)) EditRedo();
				else EditUndo();
			}
		} else {
			// Processing of normal text input is now done in OnGUI.
		}
	}
	
	/// <summary>
	/// Change the number of leading tabs to the given number.
	/// </summary>
	string Reindent(string sourceLine, int indentation) {
		int i;
		for (i=0; i<sourceLine.Length; i++) {
			if (!Lexer.IsWhitespace(sourceLine[i])) break;
		}
		if (indentation < 0) indentation = 0;
		else if (indentation > 16) indentation = 16;
		sourceLine = "\t\t\t\t\t\t\t\t\t\t\t\t\t\t\t\t".Substring(0, indentation)
			+ sourceLine.Substring(i);
		return sourceLine;
	}
	
	/// <summary>
	/// Reindent the given range of source lines.
	/// </summary>
	void ReindentLines(int fromLine, int toLine) {
		//Debug.Log("Reindenting lines " + fromLine + " to " + toLine);
		int outdentThis = 0, indentNext = 0;
		int indent = 0;
		if (fromLine > 0) {
			string prevLine = sourceLines[fromLine-1];
			IndentEffect(prevLine, out outdentThis, out indentNext);
			indent = Indentation(prevLine) + indentNext;
		}
		for (int lineNum = fromLine; lineNum <= toLine; lineNum++)  {
			string line = sourceLines[lineNum];
			int curIndent = Indentation(line);
			IndentEffect(line, out outdentThis, out indentNext);
			indent = indent - outdentThis;
			if (curIndent != indent) {
				UpdateLine(lineNum, Reindent(line, indent));
				if (lineNum == selAnchor.line) selAnchor.offset += indent - curIndent;
				if (lineNum == selEndpoint.line) selEndpoint.offset += indent - curIndent;
			}
			indent += indentNext;
		}
	}
	
	/// <summary>
	/// If we have an extended selection, replace it with the given text.
	/// Otherwise, insert the given text at the caret position.
	/// Also, update the undo/redo stack.
	/// </summary>
	/// <param name="s"></param>
	void SetSelText(string s) {
		s = s.Replace("\r\n", "\n");
		s = s.Replace("\r", "\n");
		
		bool linesInsertedOrRemoved = false;
		
		if (Time.time - lastEditTime > 1) {
			// This is a new edit.  Make sure we can undo to the previous state.
			StoreUndo();
		} else {
			// This is a continuation of the previous edit.  No need to store a new undo state.
		}
		lastEditTime = Time.time;
		
		// Start by deleting the current extended selection, if any
		if (extendedSelection) {
			if (selEndpoint.line == selAnchor.line) {
				// Easy case: selection is all on one line
				string line = sourceLines[selAnchor.line];
				int startPos = Min(selAnchor.offset, selEndpoint.offset);
				int endPos = Max(selAnchor.offset, selEndpoint.offset);
				try {
					line = line.Substring(0, startPos) + line.Substring(endPos);
				} catch (System.Exception e) {
					Debug.LogError("Got " + e + " while trying to cut from " + startPos + " to " + endPos + " in line " + line.Length + " long");
				}
				//Debug.Log("Cut from " + startPos + " to " + endPos + ", leaving " + line);
				UpdateLine(selAnchor.line, line);
				selAnchor.offset = startPos;
			} else {
				// Harder case: multi-line selection.
				TextPosition startPos = selEndFirst;
				TextPosition endPos = selEndLast;
				// First, combine the end line with the start line and the new text.
				string line = "";
				try {
					line = sourceLines[startPos.line].Substring(0, startPos.offset)
						+ sourceLines[endPos.line].Substring(endPos.offset);
				} catch (System.Exception e) {
					Debug.LogError("Got " + e + " while trying to cut from " + startPos 
						+ " to " + endPos + " in lines " 
						+ sourceLines[startPos.line].Length + " and "
						+ sourceLines[endPos.line].Length + " long");
					
				}
				UpdateLine(startPos.line, line);
				selAnchor = startPos;
				// Then, delete the middle-to-end lines entirely.
				for (int i=endPos.line; i > startPos.line; i--) {
					DeleteLine(i);
					endPos.line--;
				}
				linesInsertedOrRemoved = true;
			}
			selEndpoint = selAnchor;
		}
		
		// Then, insert the given text (which may be multiple lines)
		int reindentFrom = selAnchor.line;
		int reindentTo = selAnchor.line;
		if (!string.IsNullOrEmpty(s)) {
			string[] lines = s.Split('\n');
			for (int i=0; i<lines.Length; i++) {
				if (i < lines.Length-1) {
					// Insert this line, followed by a line break
					string src = sourceLines[selAnchor.line].Substring(0, selAnchor.offset) + lines[i];
					string nextSrc = sourceLines[selAnchor.line].Substring(selAnchor.offset);
					UpdateLine(selAnchor.line, src);
					InsertLine(selAnchor.line+1, nextSrc);
					selAnchor.line++;
					selAnchor.offset = 0;
					linesInsertedOrRemoved = true;
					// ...and then strip leading whitespace from the next line we're going to paste
					lines[i+1] = lines[i+1].TrimStart();
				} else {
					// Insert this text without a line break
					string src = sourceLines[selAnchor.line];
					if (selAnchor.offset > src.Length) selAnchor.offset = src.Length;
					src = src.Substring(0, selAnchor.offset)
						+ lines[i] + src.Substring(selAnchor.offset);
					UpdateLine(selAnchor.line, src);
					selAnchor.offset += lines[i].Length;
				}
				selEndpoint = selAnchor;
			}
		}
		
		// Reindent lines in the affected range.
		reindentTo = sourceLines.Count - 1;		// ToDo: something smarter here!
		ReindentLines(reindentFrom, reindentTo);
		
		// Adjust line Y positions if needed
		if (linesInsertedOrRemoved) UpdateLineYPositions();
		
		// And we're just about done.
		AdjustContentSize();
		UpdateSelectionDisplay();
		preferredX = caret.rectTransform.anchoredPosition.x;
		CaretOn();
	}
		
	/// <summary>
	/// Set the text of the given Text or TextMeshProUGUI.
	/// </summary>
	void SetText(Graphic uiText, string s, bool applyMarkup=true, bool sizeToFit=true) {
		uiText.gameObject.name = s;
		if (applyMarkup && style != null) s = style.Markup(s);
		
		if (uiText is Text) {
			((Text)uiText).text = s;
			if (sizeToFit) {
				// ToDo: set sizeDelta to fit the text
			}
		} else if (uiText is TextMeshProUGUI) {
			var tmPro = uiText as TextMeshProUGUI;
			tmPro.text = s;
			if (sizeToFit) {
				tmPro.ForceMeshUpdate();
				uiText.rectTransform.sizeDelta = new Vector2(tmPro.preferredWidth + 40, uiText.rectTransform.sizeDelta.y);
			}
		}
		else Debug.Assert(false, "UI text object must be either Text or TextMeshProUGUI");
	}
	
	void StoreUndo() {
		var undo = GetUndoState();
		
		undoPosition++;
		if (undoPosition >= undoStack.Count) {
			undoStack.Add(undo);
			if (undoStack.Count > undoLimit) undoStack.RemoveAt(0);
		} else {
			undoStack[undoPosition] = undo;
			if (undoPosition+1 < undoStack.Count) {
				undoStack.RemoveRange(undoPosition+1, undoStack.Count - (undoPosition+1));
			}
		}
	}
	
	/// <summary>
	/// Update the text of the given line, as it appears in the UI.
	/// </summary>
	void UpdateLine(int lineNum, string lineText) {
		sourceLines[lineNum] = lineText;
		SetText(uiTexts[lineNum], lineText);
	}
	
	/// <summary>
	/// Update the Y positions of our source lines, and also update line
	/// numbers if we have them.
	/// </summary>
	void UpdateLineYPositions() {
		float y = 0;
		float dy = sourceCodeLinePrototype.rectTransform.sizeDelta.y;
		for (int i=0; i<uiTexts.Count; i++) {
			uiTexts[i].rectTransform.anchoredPosition = new Vector2(
				uiTexts[i].rectTransform.anchoredPosition.x, y);
			
			if (lineNumberPrototype != null) {
				if (lineNums == null) lineNums = new List<Graphic>();
				if (i >= lineNums.Count) {
					var noob = Instantiate(lineNumberPrototype, lineNumberPrototype.transform.parent);
					SetText(noob, (i + lineNumbersStartAt).ToString(lineNumberFormatString), false, false);
					lineNums.Add(noob);
				}
				lineNums[i].rectTransform.anchoredPosition = new Vector2(
					lineNums[i].rectTransform.anchoredPosition.x, y);
				lineNums[i].gameObject.SetActive(true);
			}

			y -= dy;
		}
		
		if (lineNums != null) {
			for (int i = uiTexts.Count; i < lineNums.Count; i++) {
				lineNums[i].gameObject.SetActive(false);
			}
		}
		
		AdjustContentSize();
	}
	
	/// <summary>
	/// Update the visual display of the selection (the caret or selection highlight).
	/// </summary>
	public void UpdateSelectionDisplay() {
		caret.rectTransform.anchoredPosition = new Vector2(
			InsertionPointX(selEndpoint.line, selEndpoint.offset),
				uiTexts[selEndpoint.line].rectTransform.anchoredPosition.y);
		
		if (extendedSelection) {
			TextPosition startPos = selEndFirst;
			TextPosition endPos = selEndLast;
			float left = sourceCodeLinePrototype.rectTransform.anchoredPosition.x - 4;
			// First partial line
			float x0 = InsertionPointX(startPos.line, startPos.offset);
			float x1 = (transform as RectTransform).rect.width;
			if (endPos.line == startPos.line) x1 = InsertionPointX(endPos.line, endPos.offset);
			var rt = selectionHighlight.rectTransform;
			rt.anchoredPosition = new Vector2(
				x0, uiTexts[startPos.line].rectTransform.anchoredPosition.y);
			rt.sizeDelta = new Vector2(x1 - x0, uiTexts[startPos.line].rectTransform.sizeDelta.y);
			selectionHighlight.gameObject.SetActive(true);
			
			if (endPos.line > startPos.line) {
				// Last partial line
				x0 = left;
				x1 = InsertionPointX(endPos.line, endPos.offset);
				rt = selHighlight2.rectTransform;
				rt.anchoredPosition = new Vector2(
					x0, uiTexts[endPos.line].rectTransform.anchoredPosition.y);
				rt.sizeDelta = new Vector2(x1 - x0, uiTexts[endPos.line].rectTransform.sizeDelta.y);
				selHighlight2.gameObject.SetActive(true);
			} else selHighlight2.gameObject.SetActive(false);
			
			if (endPos.line > startPos.line + 1) {
				// Middle full line(s)
				x0 = left;
				x1 = (transform as RectTransform).rect.width;
				float y0 = uiTexts[startPos.line+1].rectTransform.anchoredPosition.y;
				float y1 = uiTexts[endPos.line].rectTransform.anchoredPosition.y;
				rt = selHighlightMid.rectTransform;
				rt.anchoredPosition = new Vector2(x0, y0);
				rt.sizeDelta = new Vector2(x1 - x0, Mathf.Abs(y1 - y0));
				selHighlightMid.gameObject.SetActive(true);
			} else selHighlightMid.gameObject.SetActive(false);
			
			caret.gameObject.SetActive(false);
		} else {
			// No extended selection: just show the caret
			caret.rectTransform.SetAsLastSibling();
			caret.gameObject.SetActive(true);
			selectionHighlight.gameObject.SetActive(false);
			selHighlight2.gameObject.SetActive(false);
			selHighlightMid.gameObject.SetActive(false);
		}
	}
	
	private static float WidthInFont(string str, Font font, int fontSize) {
		float totalWidth = 0;
		foreach(char c in str.ToCharArray()) {
			CharacterInfo characterInfo;
			font.GetCharacterInfo(c, out characterInfo, fontSize);  			
			totalWidth += characterInfo.advance;
		}
		
		return totalWidth;
	}


	#endregion
	
}
	
}