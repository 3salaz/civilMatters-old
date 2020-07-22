/* This file defines markup tags applied to style MiniScript code,
and the C# code needed to actually apply that markup.  If you're
using a classic Text object for your source code lines, then stick
to the basic <b>, <i>, and <color> tags.  If you're using TMPro,
then you have access to pretty much any of these:
	http://digitalnativestudios.com/textmeshpro/docs/rich-text/
*/
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Miniscript {
	
public class CodeStyling : MonoBehaviour {
	
	[System.Serializable]
	public class Style {
		public string startTags;
		public string endTags;
		
		public Style(string startTags, string endTags) {
			this.startTags = startTags;
			this.endTags = endTags;
		}
	}
	
	[Header("Styles")]
	public Style identifier = new Style("", "");
	public Style operators = new Style("<color=#4444AA>", "</color>");
	public Style stringLiteral = new Style("<color=#AA4444><noparse>", "</noparse></color>");
	public Style comment = new Style("<i><color=#666666><noparse>", "</noparse></i></color>");
	public Style numericLiteral = new Style("<color=#44AA44>", "</color>");
	public Style keyword = new Style("<color=#AA44AA>", "</color>");
	public Style openString = new Style("<color=#CC0000><noparse>", "</noparse></color>");
	public Style colon = new Style("<color=#FF00FF>", "</color>");
	
	[Header("Other Options")]
	public bool rotatingParenColors = true;
	public Color baseParenColor = new Color(0, 0, 0.8f);
	public bool rotatingSquareColors = true;
	public Color baseSquareColor = new Color(0.1f, 0.1f, 0.5f);
	
	public string Markup(string code) {
		var sb = new StringBuilder();
		var lexer = new Lexer(code);
		int parenDepth = 0, squareDepth = 0;
		bool statementStart = true;
		bool ifStatement = false;
		bool justSawThen = false;
		
		while (!lexer.AtEnd) {
			int start = lexer.position;
			
			// grab whitespace (normally skipped by the lexer)
			if (Lexer.IsWhitespace(code[lexer.position])) {
				while (!lexer.AtEnd && Lexer.IsWhitespace(code[lexer.position])) lexer.position++;
				sb.Append(code.Substring(start, lexer.position - start));
				if (lexer.AtEnd) break;
				start = lexer.position;
			}
			
			// also check for a comment (which the lexer would also skip over)
			if (lexer.position < code.Length-2 && code[lexer.position]=='/' && code[lexer.position+1]=='/') {
				while (!lexer.AtEnd && code[lexer.position] != '\n') lexer.position++;
				sb.Append(comment.startTags);
				sb.Append(code.Substring(start, lexer.position - start));
				sb.Append(comment.endTags);
				if (lexer.AtEnd) break;
				start = lexer.position;
			}
			
			// now, grab and process the next token (being sure to catch and handle lexer exceptions)
			Token tok = null;
			try {
				tok = lexer.Dequeue();
			} catch (LexerException exc) {
				tok = new Token();
				lexer.position = code.Length;
			}
			
			if (tok.text == "self") tok.type = Token.Type.Keyword;	// (special case)
			
			switch (tok.type) {
			case Token.Type.Keyword:
				sb.Append(keyword.startTags);
				sb.Append(tok.text);
				sb.Append(keyword.endTags);
				break;
			case Token.Type.Colon:
				sb.Append(colon.startTags);
				sb.Append(":");
				sb.Append(colon.endTags);
				statementStart = true;
				break;
			case Token.Type.Identifier:
				sb.Append(identifier.startTags);
				sb.Append(tok.text);
				sb.Append(identifier.endTags);
				break;
			case Token.Type.String:
				sb.Append(stringLiteral.startTags);
				sb.Append("\"");	// (note that lexer strips the surrounding quotes)
				sb.Append(tok.text.Replace("\"", "\"\""));	// and un-doubles internal quotes
				sb.Append("\"");
				sb.Append(stringLiteral.endTags);
				break;
			case Token.Type.Number:
				sb.Append(numericLiteral.startTags);
				sb.Append(tok.text);
				sb.Append(numericLiteral.endTags);
				break;
			case Token.Type.LParen:
			case Token.Type.RParen:
				if (tok.type == Token.Type.LParen) parenDepth++;
				if (rotatingParenColors) {
					float h, s, v;
					Color.RGBToHSV(baseParenColor, out h, out s, out v);
					h = Mathf.Repeat(h + 0.22f * (parenDepth-1), 1);
					Color color = Color.HSVToRGB(h, s, v);
					if (parenDepth < 1) color = Color.red;
					sb.Append("<color=#");
					sb.Append(ColorUtility.ToHtmlStringRGB(color));
					sb.Append(tok.type == Token.Type.LParen ? ">(</color>" : ">)</color>");
				} else {
					sb.Append(tok.type == Token.Type.LParen ? ">(</color>" : ">)</color>");					
				}
				if (tok.type == Token.Type.RParen) parenDepth--;
				break;
			case Token.Type.LSquare:
			case Token.Type.RSquare:
				if (tok.type == Token.Type.LSquare) squareDepth++;
				if (rotatingSquareColors) {
					float h, s, v;
					Color.RGBToHSV(baseSquareColor, out h, out s, out v);
					h = Mathf.Repeat(h + 0.22f * (squareDepth-1), 1);
					Color color = Color.HSVToRGB(h, s, v);
					if (squareDepth < 1) color = Color.red;
					sb.Append("<color=#");
					sb.Append(ColorUtility.ToHtmlStringRGB(color));
					sb.Append(tok.type == Token.Type.LSquare ? ">[</color>" : ">]</color>");
				} else {
					sb.Append(tok.type == Token.Type.LSquare ? ">[</color>" : ">]</color>");					
				}
				if (tok.type == Token.Type.RSquare) squareDepth--;
				break;
			case Token.Type.Unknown:
				if (code[start] == '"') {
					sb.Append(openString.startTags);
					sb.Append(code.Substring(start, lexer.position - start));
					sb.Append(openString.endTags);
				} else {
					sb.Append(code.Substring(start, lexer.position - start));					
				}
				break;
			default:
				sb.Append(operators.startTags);
				sb.Append(code.Substring(start, lexer.position - start));
				sb.Append(operators.endTags);
				break;
			}
			statementStart = false;
		}
		return sb.ToString();
	}
}
	
}