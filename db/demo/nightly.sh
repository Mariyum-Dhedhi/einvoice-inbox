#!/bin/sh
# Resets the public demo every night at RESET_AT (default 03:00), Berlin time, whatever the season.
#   nightly.sh                               wait for the next RESET_AT, reset, repeat (the demo-reset service)
#   nightly.sh now                           reset once, now
#   nightly.sh --seconds-until-next <time>   print how long the wait would be from <time> (tests)
set -eu

AT="${RESET_AT:-03:00}"
case "$AT" in
  [01][0-9]:[0-5][0-9]|2[0-3]:[0-5][0-9]) ;;
  *) echo "RESET_AT must be HH:MM." >&2; exit 1 ;;
esac
TZ=Europe/Berlin
export TZ

# The next RESET_AT after $1 (seconds since 1970), on the Berlin calendar: today's if still ahead, else tomorrow's.
next_reset() {
  today=$(date -d "@$1" +%F)
  next=$(date -d "$today $AT" +%s)
  if [ "$next" -le "$1" ]; then
    # Tomorrow's date from today's noon: 24 hours after noon is the next day even when the clocks change tonight.
    tomorrow=$(date -d "@$(( $(date -d "$today 12:00" +%s) + 86400 ))" +%F)
    next=$(date -d "$tomorrow $AT" +%s)
  fi
  echo "$next"
}

case "${1:-}" in
  --seconds-until-next)
    now=$(date -d "$2" +%s)
    echo $(( $(next_reset "$now") - now ))
    exit 0 ;;
  now)
    exec /bin/sh /db/demo/reset.sh ;;
  '') ;;
  *) echo "Unknown argument: $1" >&2; exit 1 ;;
esac

while true; do
  target=$(next_reset "$(date +%s)")
  echo "Next reset: $(date -d "@$target" '+%F %H:%M %Z')"
  # A minute at a time against the clock, not one long sleep: after the PC slept through RESET_AT, the reset still runs.
  while [ "$(date +%s)" -lt "$target" ]; do
    sleep 60
  done
  /bin/sh /db/demo/reset.sh || echo "Reset failed; the next try is at the next $AT."
done
