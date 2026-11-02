ECR Simulator 2.0  -  created by Ahmad El Lahib
================================================

A Windows cash register (ECR) that talks to the Areeba POS over a cable.
Replaces the "Mega Tech ECR Simulator" (C:\Android Dev new\megatech-simulator).

Start: double-click "ECR Simulator.cmd"
       (or: powershell -STA -ExecutionPolicy Bypass -File Start-EcrSimulator.ps1 -Port COM21)
No .exe is needed: the sources in src\ are compiled in memory, so the antivirus has nothing to delete.
build.cmd makes "ECR Simulator.exe" if you want one.

1. Connection
   Cable:  Nexgo D20 Bluetooth base (USB)  -> 115200 8N1, RTS 0, DTR 0 (RTS 1 reboots the D20)
           Direct cable to the terminal    -> 9600 8N1, RTS 1, DTR 1
           Custom                          -> any baud / data / parity / stop / RTS / DTR
   Mode:   Always open  - press Connect, the port stays open
           Open per transaction - like the duty free Mega Tech register: open, send, wait for the result, close
   RTS / DTR changes apply at once on an open port.

2. Protocol: QS, HDMC, KV (direct), KV Jordan (progressive messages + delivery ack)
   - optional connection check before each transaction (IsConnected? for QS/HDMC, TXNID=0 keep-alive for KV)
   - result timeout, auto check every N s
   - KV Jordan: the delivery ack can be turned off or delayed (> 1500 ms) to test the POS reversal

3. Transaction: Sale, Preauth, Completion, Refund, Void, Settlement, connection check, Stop waiting.
   Original ref (and the KV TXNID) fill in by themselves after an approval, for Void / Completion.
   Repeat N times every X s for soak tests. A stale QS result (other EcrRefNo) is flagged and ignored.

4. Fault tests: stop reading the port for N s, close the port for N s.

Free text: send anything on the cable. Frames: as typed, QS register frame (STX..ETX+LRC),
POS style (STX..ETX CR LF), KV frame (fields separated by commas). Escapes like <STX> <ETX> <CR> <LF>
<ACK> <1C> \x02, or type hex bytes. Repeat with an interval. Last 25 sends are in History.

Logs: logs\ecr-<date>-<time>.log (every byte, control characters shown as <STX> etc.).
Settings are saved in ecr-simulator.ini when the window closes.
Only one program can use a COM port at a time.
