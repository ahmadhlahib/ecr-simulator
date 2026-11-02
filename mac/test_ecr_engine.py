"""Tests for the ECR Simulator engine with a fake serial port. Run: python3 -m unittest test_ecr_engine"""

import os
import sys
import unittest
from decimal import Decimal

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "ECR Simulator.app", "Contents", "Resources"))
import ecr_engine as E  # noqa: E402

POS_STX, POS_ETX = "\x02", "\x03"


class FakePort:
    """Plays the terminal: records what the register writes, hands back queued replies."""

    def __init__(self):
        self.written = []
        self.replies = []
        self.closed = False
        self.before = "fake"

    def write(self, b):
        self.written.append(b.decode("latin-1"))

    def read(self):
        return self.replies.pop(0).encode("latin-1") if self.replies else b""

    def set_rts(self, on):
        pass

    def set_dtr(self, on):
        pass

    def close(self):
        self.closed = True


class Clock:
    def __init__(self):
        self.t = 1000.0

    def __call__(self):
        return self.t


def pos(text):
    """A POS -> register QS message: STX text ETX CR LF."""
    return POS_STX + text + POS_ETX + "\r\n"


class EngineTest(unittest.TestCase):
    def setUp(self):
        self.port = FakePort()
        self.opens = 0
        self.clock = Clock()

        def open_port(s):
            self.opens += 1
            self.port.closed = False
            return self.port
        self.engine = E.Engine(open_port, lambda p: True, None, now=self.clock)

    def settings(self, **kw):
        s = E.Settings(port="/dev/cu.test")
        for k, v in kw.items():
            setattr(s, k, v)
        self.engine.apply(s)
        self.run_steps()

    def run_steps(self, n=5):
        for _ in range(n):
            self.engine.step()

    def sale(self, **kw):
        r = E.TxnRequest(type="Sale", amount=Decimal("5.00"), currency="840", ref="00151117")
        for k, v in kw.items():
            setattr(r, k, v)
        self.engine.start_txn(r)
        self.run_steps()

    def test_qs_frame_lrc_matches_the_real_register(self):
        # From the bench log of 2026-10-29: <STX>InitiateTxn=Sale&00151114000000000500840<ETX>z
        self.assertEqual(E.qs_frame("InitiateTxn=Sale&00151114000000000500840")[-1], "z")

    def test_qs_sale_sends_ref_amount_and_currency(self):
        self.settings()
        self.sale()
        self.assertEqual(self.port.written[-1], E.qs_frame("InitiateTxn=Sale&00151117000000000500840"))

    def test_qs_approved_sale_is_remembered_for_void_and_port_closes(self):
        self.settings(per_txn=True)
        self.sale()
        self.port.replies += [pos("Tap, Insert or Swipe Card"),
                              pos("TxnResp\r\n\nTxn=SALE\nInvoice=000000000067\nEcrRefNo=00151117 \nAmount=5.00\nCurrency=USD\nRespCode=00\nRespMsg=Approved\n")]
        self.run_steps()
        snap = self.engine.snapshot()
        self.assertEqual(snap["result_kind"], "ok")
        self.assertEqual(snap["last_sale_ref"], "00151117")
        self.assertFalse(snap["busy"])
        self.assertTrue(self.port.closed)

    def test_qs_stale_result_with_another_ref_is_ignored(self):
        self.settings()
        self.sale()
        self.port.replies.append(pos("TxnResp\r\n\nEcrRefNo=00151116 \nRespCode=00\n"))
        self.run_steps()
        self.assertTrue(self.engine.snapshot()["busy"])

    def test_qs_cancelled_ends_the_transaction(self):
        self.settings()
        self.sale()
        self.port.replies.append(pos("Cancelled"))
        self.run_steps()
        snap = self.engine.snapshot()
        self.assertEqual(snap["result_title"], "CANCELLED")
        self.assertFalse(snap["busy"])

    def test_handshake_sends_isconnected_then_the_sale_after_ok(self):
        self.settings(handshake=True)
        self.sale()
        self.assertEqual(self.port.written, ["IsConnected?"])
        self.port.replies.append(pos("ok"))
        self.run_steps()
        self.assertTrue(self.port.written[-1].startswith(POS_STX + "InitiateTxn=Sale&"))

    def test_no_result_times_out(self):
        self.settings(result_timeout_sec=120)
        self.sale()
        self.clock.t += 121
        self.run_steps()
        self.assertEqual(self.engine.snapshot()["result_title"], "NO RESULT")

    def test_void_without_original_ref_is_refused(self):
        self.settings()
        self.engine.start_txn(E.TxnRequest(type="Void"))
        self.run_steps()
        self.assertEqual(self.engine.snapshot()["result_title"], "ORIGINAL REF MISSING")
        self.assertEqual(self.port.written, [])

    def test_hdmc_sale_has_no_currency_and_two_decimals(self):
        self.settings(fmt="HDMC")
        self.sale(decimals=3)
        self.assertEqual(self.port.written[-1], E.qs_frame("InitiateTxn=Sale&00151117000000000500"))

    def test_kv_sale_fields(self):
        self.settings(fmt="KV")
        self.sale(amount=Decimal("1.5"), currency="400", decimals=3, seq_no="0001")
        self.assertEqual(self.port.written[-1],
                         '"<START>","CMD=SALE","AMT=1.500","CURRENCY=400","ECRREF=00151117","TXNCONSEQUENTIALNO=0001","<END>"')

    def test_kv_jordan_progressive_result_and_delivery_ack(self):
        self.settings(fmt="KVJordan", per_txn=False)
        self.engine.connect()
        self.run_steps()
        self.sale(amount=Decimal("1"), currency="400", decimals=3)
        full = '"<START>","STATUS=A","RESPONSECODE=00","RSPTEXT=TXN.ACCEPTED","TXNID=000123","TXNTYPE=SALE","AMT=1.000","<END>"'
        self.port.replies += [full[:30], full[:60], full]
        self.run_steps()
        snap = self.engine.snapshot()
        self.assertEqual(snap["result_kind"], "ok")
        self.assertEqual(snap["last_sale_txn_id"], "000123")
        self.assertEqual(self.port.written[-1], '"<START>","<END>"')

    def test_kv_jordan_ack_off_sends_nothing(self):
        self.settings(fmt="KVJordan", per_txn=False, kv_ack=False)
        self.engine.connect()
        self.run_steps()
        self.sale(amount=Decimal("1"))
        self.port.replies.append('"<START>","STATUS=A","RESPONSECODE=00","TXNTYPE=SALE","<END>"')
        self.run_steps()
        self.assertTrue(self.port.written[-1].startswith('"<START>","CMD=SALE"'))

    def test_kv_incremental_points_at_the_preauth_ref(self):
        self.settings(fmt="KV")
        self.engine.start_txn(E.TxnRequest(type="IncrementalPreauth", amount=Decimal("2.5"), currency="400",
                                           decimals=3, orig_ref="02124658"))
        self.run_steps()
        self.assertEqual(self.port.written[-1],
                         '"<START>","CMD=INCREMENTAL_PREAUTH","AMT=2.500","CURRENCY=400","ECRREF=02124658","<END>"')

    def test_incremental_is_refused_in_qs(self):
        self.settings(fmt="QS")
        self.engine.start_txn(E.TxnRequest(type="IncrementalPreauth", amount=Decimal("2"), orig_ref="1"))
        self.run_steps()
        self.assertEqual(self.engine.snapshot()["result_title"], "NOT IN QS")
        self.assertEqual(self.port.written, [])

    def test_kv_void_needs_txnid(self):
        self.settings(fmt="KV")
        self.engine.start_txn(E.TxnRequest(type="Void", orig_ref="1"))
        self.run_steps()
        self.assertEqual(self.engine.snapshot()["result_title"], "TXNID MISSING")

    def test_kv_keep_alive_answered_with_empty_frame(self):
        self.settings(fmt="KV")
        self.engine.check()
        self.run_steps()
        self.assertEqual(self.port.written, ['"<START>","TXNID=0","<END>"'])
        self.port.replies.append('"<START>","<END>"')
        self.run_steps()
        self.assertTrue(self.engine.snapshot()["result_title"].startswith("TERMINAL CONNECTED"))

    def test_always_open_mode_refuses_a_sale_before_connect(self):
        self.settings(per_txn=False)
        self.sale()
        self.assertEqual(self.engine.snapshot()["result_title"], "NOT CONNECTED")
        self.assertEqual(self.opens, 0)

    def test_repeat_runs_the_next_sale_with_the_next_ref(self):
        self.settings(per_txn=False)
        self.engine.connect()
        self.run_steps()
        self.sale(repeat=2, repeat_interval_sec=5)
        self.port.replies.append(pos("Cancelled"))
        self.run_steps()
        self.clock.t += 6
        self.run_steps()
        self.assertTrue(self.port.written[-1].startswith(POS_STX + "InitiateTxn=Sale&00151118"))

    def test_next_ref_wraps_at_eight_digits(self):
        self.assertEqual(E.next_ref("99999999"), "00000000")
        self.assertEqual(E.next_ref("00000041"), "00000042")


if __name__ == "__main__":
    unittest.main()
