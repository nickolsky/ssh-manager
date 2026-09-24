# Third-party components

SSH Manager is released under the MIT License (see `LICENSE`). It includes or uses these components under their own licenses:

| Component | Use | License |
|---|---|---|
| [SSH.NET](https://github.com/sshnet/SSH.NET) | SSH, SFTP, the built-in terminal | MIT |
| [Bouncy Castle for .NET](https://github.com/bcgit/bc-csharp) | Argon2, key formats | MIT |
| [Microsoft.Web.WebView2](https://www.nuget.org/packages/Microsoft.Web.WebView2) | hosts the terminal and the editor pages | BSD-3-Clause (Microsoft), see the package's LICENSE.txt |
| [xterm.js](https://github.com/xtermjs/xterm.js) and its fit, web-links, unicode11 addons | the built-in terminal (`Assets/terminal/vendor`) | MIT, `Assets/terminal/vendor/LICENSE-xterm.txt` |
| [CodeMirror 6](https://codemirror.net) with its language packages, Lezer parsers and legacy modes | the built-in editor (`Assets/editor/vendor/codemirror.js`, built from `tools/codemirror`) | MIT, `Assets/editor/vendor/LICENSE-codemirror.txt` |

The built-in install scripts are adapted from [vless_docker_install_scripts](https://github.com/nickolsky/vless_docker_install_scripts).
Icons use the Segoe Fluent Icons / Segoe MDL2 Assets fonts shipped with Windows (not redistributed).
