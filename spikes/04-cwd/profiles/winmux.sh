# WinMux cwd reporting for bash and zsh (OSC 7).
#
# Add to ~/.bashrc or ~/.zshrc:   source /path/to/winmux.sh
#
# Emits OSC 7 as a file:// URL before each prompt, the xterm convention that WSL, VS Code and
# most Linux terminals already understand. Many distributions ship an equivalent already (the
# Debian image measured in spike 4 reported OSC 7 with no snippet at all), so this is a no-op
# there rather than a duplicate.

__winmux_osc7() {
    # NOTE: a strict consumer expects the path to be percent-encoded. Plain paths with spaces
    # and non-ASCII round-tripped correctly in testing, but encode if you hit a fussy terminal.
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
