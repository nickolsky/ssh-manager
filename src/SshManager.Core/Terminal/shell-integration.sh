# SSH Manager shell integration for the built-in terminal (bash, zsh).
# Sourced once per session from ~/.cache/sshm; no rc file is changed.
# Marks (OSC 7337): A prompt start, B prompt end = input starts, D;<exit code>, P;<cwd>, E;<file> open in the
# editor, I integration loaded. Adds "edit FILE..." that opens files in the SSH Manager editor.
__sshm_edit() {
  local f
  [ $# -gt 0 ] || { echo 'usage: edit FILE...' >&2; return 2; }
  for f in "$@"; do
    case "$f" in /*) ;; *) f="$PWD/$f" ;; esac
    printf '\033]7337;E;%s\007' "$f"
  done
}

if [ -n "${BASH_VERSION-}" ] && [ -z "${__sshm_loaded-}" ]; then
  __sshm_loaded=1
  __sshm_status() { __sshm_ret=$?; }
  __sshm_prompt() {
    printf '\033]7337;D;%s\007\033]7337;P;%s\007' "${__sshm_ret:-0}" "$PWD"
    # wrapped again every time: prompt themes (starship, liquidprompt…) rebuild PS1 on each prompt
    case "$PS1" in *'7337;A'*) ;; *) PS1='\[\033]7337;A\007\]'"$PS1"'\[\033]7337;B\007\]' ;; esac
    return "${__sshm_ret:-0}"
  }
  if [[ "$(declare -p PROMPT_COMMAND 2>/dev/null)" == 'declare -a'* ]]; then
    PROMPT_COMMAND=(__sshm_status "${PROMPT_COMMAND[@]}" __sshm_prompt)
  else
    # newlines, not ';': the existing value may end with ';'
    PROMPT_COMMAND=$'__sshm_status\n'"${PROMPT_COMMAND-}"$'\n__sshm_prompt'
  fi
  alias edit='__sshm_edit' 2>/dev/null
  # keep the line that sourced this file out of the history (HISTCONTROL may not ignore leading spaces)
  __sshm_h=$(HISTTIMEFORMAT='' builtin history 1)
  case "$__sshm_h" in *'sshm/shell-'*) builtin history -d "$(( ${__sshm_h%%[!0-9 ]*} ))" 2>/dev/null ;; esac
  unset __sshm_h
  printf '\033]7337;I\007'
elif [ -n "${ZSH_VERSION-}" ] && [ -z "${__sshm_loaded-}" ]; then
  __sshm_loaded=1
  __sshm_status() { __sshm_ret=$?; }
  __sshm_precmd() {
    printf '\033]7337;D;%s\007\033]7337;P;%s\007' "${__sshm_ret:-0}" "$PWD"
    [[ "$PS1" == *'7337;A'* ]] || PS1=$'%{\e]7337;A\a%}'"$PS1"$'%{\e]7337;B\a%}'
  }
  typeset -ga precmd_functions
  precmd_functions=(__sshm_status $precmd_functions __sshm_precmd)
  alias edit='__sshm_edit'
  printf '\033]7337;I\007'
fi
