# WinMux cwd reporting for bash and zsh (OSC 7).
# The installer provides an exact manual source command and never edits WSL files itself.

__winmux_osc7() {
    printf '\033]7;file://%s%s\033\' "${HOSTNAME:-$(hostname)}" "$PWD"
}

if [ -n "$ZSH_VERSION" ]; then
    autoload -Uz add-zsh-hook 2>/dev/null && add-zsh-hook precmd __winmux_osc7
else
    case "$PROMPT_COMMAND" in
        *__winmux_osc7*) ;;
        *) PROMPT_COMMAND="__winmux_osc7${PROMPT_COMMAND:+; $PROMPT_COMMAND}" ;;
    esac
fi
