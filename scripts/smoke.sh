#!/usr/bin/env bash
# Drives the composed stack over HTTP. Every code carries a run id, so a kept Postgres volume never collides.
# Needs: curl, python3. The stack must be up: docker compose up --build --wait
set -euo pipefail

API="${API:-http://127.0.0.1:5212}"
RABBIT="${RABBIT:-http://127.0.0.1:15702}"
RUN="$(date +%s)$((RANDOM % 900 + 100))"
JSON='content-type: application/json'

say() { printf '  %s\n' "$*"; }
fail() { printf 'FAIL: %s\n' "$*" >&2; exit 1; }
jget() { python3 -c "import sys,json; d=json.load(sys.stdin); print(d$1)"; }

open_account() { # code openingCents -> prints nothing
  curl -sf -X POST "$API/api/accounts" -H "$JSON" \
    -d "{\"code\":\"$1\",\"holder\":\"Smoke Treasury\",\"currency\":\"USD\",\"branch\":\"Main Branch\",\"openingCents\":$2}" >/dev/null \
    || fail "could not open account $1"
}
send() { # code reference amountCents -> prints payment id
  curl -sf -X POST "$API/api/payments" -H "$JSON" \
    -d "{\"accountCode\":\"$1\",\"reference\":\"$2\",\"amountCents\":$3}" | jget "['id']"
}
payment_field() { curl -sf "$API/api/payments/$1" | jget "['$2']"; }
remaining() { curl -sf "$API/api/accounts/$1" | jget "['availableCents']"; }

wait_for() { # description command... : polls up to 30s until the command succeeds
  local what="$1"; shift
  for _ in $(seq 1 60); do
    if "$@" >/dev/null 2>&1; then return 0; fi
    sleep 0.5
  done
  fail "timed out waiting for $what"
}
status_is() { [ "$(payment_field "$1" status)" = "$2" ]; }
remaining_is() { [ "$(remaining "$1")" = "$2" ]; }
terminal() { case "$(payment_field "$1" status)" in Settled|Rejected|Reversed) return 0 ;; *) return 1 ;; esac; }
event_types() { curl -sf "$API/api/payments/$1/events" | python3 -c "import sys,json; print(' '.join(e['type'] for e in json.load(sys.stdin)))"; }
dlq_is() { [ "$(dlq_count)" = "$1" ]; }
dlq_count() { curl -sf -u platform:platform "$RABBIT/api/queues/%2F/receipts.work.dlq" | jget "['messages']"; }

curl -sf "$API/api/health" >/dev/null || fail "API is not healthy at $API; run: docker compose up --build --wait"

echo "1. input is validated at the door"
open_account "V-$RUN" 1000000
for body in \
  "{\"accountCode\":\"V-$RUN\",\"reference\":\"\",\"amountCents\":1}" \
  "{\"accountCode\":\"V-$RUN\",\"reference\":\"R-$RUN\",\"amountCents\":0}" \
  "{\"accountCode\":\"V-$RUN\",\"reference\":\"R-$RUN\",\"amountCents\":5000001}" \
  "{\"accountCode\":\"V-$RUN\",\"reference\":\"R-$RUN\",\"amountCents\":4294967297}" \
  "{\"accountCode\":\"V-$RUN\",\"reference\":\"R-$RUN\",\"amountCents\":9223372036854775807}"; do
  code="$(curl -s -o /dev/null -w '%{http_code}' -X POST "$API/api/payments" -H "$JSON" -d "$body")"
  [ "$code" = 400 ] || fail "expected 400 for $body, got $code"
done
say "empty, zero, over-limit and overflowing amounts are all 400"

echo "2. happy path"
open_account "H-$RUN" 100000
ID="$(send "H-$RUN" "PAY-$RUN-1" 30000)"
wait_for "payment to be Settled" status_is "$ID" Settled
[ "$(payment_field "$ID" feeCents)" = 200 ] || fail "fee should be 50 cents + 0.5% of 30000 = 200"
[ "$(curl -sf "$API/api/receipts/$ID" | jget "['status']")" = Issued ] || fail "receipt not issued"
wait_for "available cents to be 69800" remaining_is "H-$RUN" 69800
TYPES="$(event_types "$ID")"
for t in funds.reserve funds.held receipt.issued; do
  case "$TYPES" in *"$t"*) ;; *) fail "Kafka log is missing $t (has: $TYPES)" ;; esac
done
say "settled, fee 200, available 69800, Kafka log has the three steps"

echo "3. last funds: three payments of 10000 for 10100 available, one settles, two are short and write no hold"
open_account "L-$RUN" 10100
TMP="$(mktemp -d)"
for n in A B C; do ( send "L-$RUN" "PAY-$RUN-$n" 10000 >"$TMP/$n" ) & done
wait
IDS=""
for n in A B C; do [ -s "$TMP/$n" ] || fail "payment $n was not accepted"; IDS="$IDS $(cat "$TMP/$n")"; done
rm -rf "$TMP"
for i in $IDS; do wait_for "payment $i to settle" terminal "$i"; done
SETTLED=0; SHORT=0
for i in $IDS; do
  s="$(payment_field "$i" status)"
  if [ "$s" = Settled ]; then SETTLED=$((SETTLED + 1)); fi
  if [ "$s" = Rejected ]; then
    case "$(payment_field "$i" reason)" in Short*) SHORT=$((SHORT + 1)); case "$(event_types "$i")" in *funds.held*) fail "the short loser has a hold" ;; esac ;; esac
  fi
done
[ "$SETTLED" = 1 ] && [ "$SHORT" = 2 ] || fail "expected 1 settled and 2 short, got $SETTLED settled and $SHORT short"
[ "$(remaining "L-$RUN")" = 0 ] || fail "available should be 0"
say "1 settled, 2 short, available 0, no hold for the losers"

echo "4. poison: committed as rejected, dead-lettered, the funds come back"
open_account "P-$RUN" 50000
BEFORE="$(dlq_count)"
IDP="$(send "P-$RUN" "PAY-POISON-$RUN" 20000)"
wait_for "poison payment to be Rejected" status_is "$IDP" Rejected
wait_for "available cents to be restored to 50000" remaining_is "P-$RUN" 50000
wait_for "receipts.work.dlq to gain one message" dlq_is $((BEFORE + 1))
[ "$(curl -sf "$API/api/receipts/$IDP" | jget "['status']")" = Rejected ] || fail "rejection should be committed on the receipt row"
[ "$(payment_field "$IDP" fundsReleasedAt)" != None ] || fail "payment should record the release"
say "rejected, dead-lettered (+1 in receipts.work.dlq), available back to 50000"

echo "5. reversal races the final mark: the reversal wins, the issued receipt stays"
open_account "R-$RUN" 60000
WON=0
for attempt in 1 2 3; do
  IDR="$(send "R-$RUN" "PAY-$RUN-R$attempt" 20000)"
  # The receipt exists before the payment is marked Settled (the final mark is delayed in compose).
  wait_for "receipt to exist" curl -sf "$API/api/receipts/$IDR"
  if status_is "$IDR" Held; then WON=1; break; fi
  say "window missed on attempt $attempt (payment already Settled); the reversal still has to hold; retrying"
  curl -sf -X POST "$API/api/payments/$IDR/reversal" >/dev/null || true
  wait_for "payment to be Reversed" status_is "$IDR" Reversed
done
[ "$WON" = 1 ] || fail "never caught the payment between receipt issued and final mark"
curl -sf -X POST "$API/api/payments/$IDR/reversal" >/dev/null || fail "reversal was refused"
wait_for "payment to be Reversed" status_is "$IDR" Reversed
sleep 3 # longer than the final-mark delay: the late mark must not undo the reversal
status_is "$IDR" Reversed || fail "the final mark overwrote the reversal"
[ "$(curl -sf "$API/api/receipts/$IDR" | jget "['status']")" = Issued ] || fail "the issued receipt was rolled back"
[ "$(payment_field "$IDR" receiptNumber)" = None ] || fail "the late final mark should have been skipped"
wait_for "funds to come back" remaining_is "R-$RUN" 60000
code="$(curl -s -o /dev/null -w '%{http_code}' -X POST "$API/api/payments/$IDR/reversal")"
[ "$code" = 409 ] || fail "second reversal should be 409, got $code"
say "payment Reversed, receipt still Issued, available 60000, second reversal 409"

echo "ledger smoke passed"
