# MONITORING.md

This file tells Claude Code how to monitor and log work for this project.

**Location of logs:**
```
~/.claude-monitor-system/logs/AlignmentReforge.monitor/
```

---

## Logging Commands (Copy & Paste)

### Start a task
```bash
python ~/.claude-monitor-system/log.py task update "[Task Name]" "in_progress"
```

Example:
```bash
python ~/.claude-monitor-system/log.py task update "Case 2 solver: fix ComputeCurvatureProfile" "in_progress"
```

### Log a file change
```bash
python ~/.claude-monitor-system/log.py file modified "[filepath]" "[reason]"
```

Example:
```bash
python ~/.claude-monitor-system/log.py file modified "src/AlignmentReforge.Geometry/Case2Solver.cs" "Replaced ComputeCurvatureProfile with correct osculating curvature formula"
```

### Log a command execution
```bash
python ~/.claude-monitor-system/log.py command "[command]" "[exit_code]" "[reason]"
```

Example after running a test:
```bash
dotnet run --project src/AlignmentReforge.Console -- selfcheck-case2
python ~/.claude-monitor-system/log.py command "dotnet run selfcheck-case2" "0" "All 4 curves verified"
```

### Finish a task
```bash
python ~/.claude-monitor-system/log.py task update "[Task Name]" "done" "[completion details]"
```

Example:
```bash
python ~/.claude-monitor-system/log.py task update "Case 2 solver: fix ComputeCurvatureProfile" "done" "Implementation complete, all 4 curves passing validation"
```

---

## Integration Pattern for Claude Code

When working on this project, include this in every prompt:

```
MONITORING (do this as you work):

When you START:
  python ~/.claude-monitor-system/log.py task update "[Task name from CLAUDE.md]" "in_progress"

When you MODIFY files:
  python ~/.claude-monitor-system/log.py file modified "[file]" "[brief reason]"

When you RUN TESTS:
  [test command]
  python ~/.claude-monitor-system/log.py command "[test command]" "[exit_code]" "[result summary]"

When DONE:
  python ~/.claude-monitor-system/log.py task update "[Task name]" "done" "[what was accomplished]"
```

---

## View Dashboard

From any terminal:

```bash
# Quick view
python ~/.claude-monitor-system/dashboard.py --project AlignmentReforge

# Live refresh
watch -n 2 'python ~/.claude-monitor-system/dashboard.py --project AlignmentReforge'

# If alias is set up
cc-monitor --project AlignmentReforge
```

---

## What You'll See

Dashboard shows:
- **Tasks**: How many are done/in-progress/blocked
- **Recent Actions**: Files changed, commands run, decisions made
- **Token Usage**: Costs by model (Opus, Sonnet, Haiku)
- **Stuck Detection**: Alerts if task hangs or loops

---

## If Something Goes Wrong

### Task stuck for > 10 minutes
Check dashboard → see what command is running → go to Claude Code and read the error → fix and restart

### Command repeating 5+ times
Usually a test failure being retried. Check error message, debug in chat Claude, fix, restart Claude Code.

### Token warning
Check: `cat ~/.claude-monitor-system/logs/AlignmentReforge.monitor/tokens.json | jq '.total'`

---

## Setup Checklist

Before working, ensure:

- [ ] `~/.claude-monitor-system/` exists with the 3 Python files
- [ ] `~/.claude-monitor-system/logs/AlignmentReforge.monitor/` directory exists
- [ ] This file (MONITORING.md) is in the project root
- [ ] CLAUDE.md describes the current work
- [ ] Dashboard alias `cc-monitor` is set up (optional but convenient)

---

## One-Time System Setup

If you haven't set up the centralized system yet:

```bash
# Create directories
mkdir -p ~/.claude-monitor-system/logs/AlignmentReforge.monitor

# Copy the Python files
cp claude-code-monitor.py ~/.claude-monitor-system/
cp log-helper.py ~/.claude-monitor-system/log.py
cp dashboard.py ~/.claude-monitor-system/

# Add to ~/.bashrc or ~/.zshrc (optional but recommended)
cat >> ~/.bashrc << 'EOF'
export CLAUDE_MONITOR_ROOT="$HOME/.claude-monitor-system"
alias cc-monitor="python $CLAUDE_MONITOR_ROOT/dashboard.py"
alias cc-log="python $CLAUDE_MONITOR_ROOT/log.py"
EOF

source ~/.bashrc
```

After that, this file is all Claude Code needs. It knows where to log.
