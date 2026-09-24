// Bundle entry for the SSH Manager editor: CodeMirror 6 with the languages a server config needs.
import { basicSetup } from 'codemirror';
import { EditorState, Compartment } from '@codemirror/state';
import { EditorView, keymap } from '@codemirror/view';
import { indentWithTab } from '@codemirror/commands';
import { StreamLanguage, indentUnit } from '@codemirror/language';
import { openSearchPanel, gotoLine } from '@codemirror/search';
import { oneDark } from '@codemirror/theme-one-dark';
import { javascript } from '@codemirror/lang-javascript';
import { json } from '@codemirror/lang-json';
import { yaml } from '@codemirror/lang-yaml';
import { python } from '@codemirror/lang-python';
import { html } from '@codemirror/lang-html';
import { css } from '@codemirror/lang-css';
import { xml } from '@codemirror/lang-xml';
import { markdown } from '@codemirror/lang-markdown';
import { sql } from '@codemirror/lang-sql';
import { shell } from '@codemirror/legacy-modes/mode/shell';
import { nginx } from '@codemirror/legacy-modes/mode/nginx';
import { properties } from '@codemirror/legacy-modes/mode/properties';
import { toml } from '@codemirror/legacy-modes/mode/toml';
import { dockerFile } from '@codemirror/legacy-modes/mode/dockerfile';
import { lua } from '@codemirror/legacy-modes/mode/lua';
import { go } from '@codemirror/legacy-modes/mode/go';
import { perl } from '@codemirror/legacy-modes/mode/perl';
import { diff } from '@codemirror/legacy-modes/mode/diff';

const legacy = (m) => () => StreamLanguage.define(m);

window.CM = {
  basicSetup, EditorState, Compartment, EditorView, keymap, indentWithTab, indentUnit, openSearchPanel, gotoLine, oneDark,
  languages: {
    javascript: () => javascript(), typescript: () => javascript({ typescript: true }), json: () => json(), yaml: () => yaml(),
    python: () => python(), html: () => html(), css: () => css(), xml: () => xml(), markdown: () => markdown(), sql: () => sql(),
    shell: legacy(shell), nginx: legacy(nginx), properties: legacy(properties), toml: legacy(toml), dockerfile: legacy(dockerFile),
    lua: legacy(lua), go: legacy(go), perl: legacy(perl), diff: legacy(diff),
  },
};
