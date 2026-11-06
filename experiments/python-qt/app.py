# Stack: Python 3.12 + PySide6 (Qt), both already on this PC. QTableView over a model that makes rows on demand.
# Driven by the watcher through stdin; prints only timings.
import random
import statistics
import sys
import threading
import time

from PySide6.QtCore import QAbstractTableModel, QModelIndex, QObject, Qt, QTimer, Signal
from PySide6.QtWidgets import QAbstractItemView, QApplication, QHBoxLayout, QListWidget, QMainWindow, QTableView, QToolBar, QWidget

ROWS, COLS = 1_000_000, 20
PLACES = ["مخزن القاهرة", "Cairo Store", "مصنع ٦ أكتوبر", "Line B - خط ب", "الإسكندرية", "Plant 7", "مخزن رقم 12", "Giza Hub"]
ITEMS = ["مسمار صلب 10 مم", "Steel bolt M8", "غطاء بلاستيك", "Cable 2.5 mm", "محرك صغير", "Valve A-17", "لوحة تحكم", "Bearing 6204"]
M = 0xFFFFFFFF


def cell(r, c):
    h = ((r * 2654435761) & M) ^ ((c * 40503) & M)
    h ^= h >> 13
    h = (h * 2246822519) & M
    h ^= h >> 16
    k = c % 5
    if k == 0:
        return str(100000 + r)
    if k == 1:
        return PLACES[h % 8]
    if k == 2:
        return ITEMS[(h >> 3) % 8]
    if k == 3:
        return f"{(h % 10000000) / 100:,.2f}"
    return f"2026-{1 + h % 12:02d}-{1 + (h >> 5) % 28:02d}"


class Model(QAbstractTableModel):
    def rowCount(self, parent=QModelIndex()):
        return 0 if parent.isValid() else ROWS

    def columnCount(self, parent=QModelIndex()):
        return 0 if parent.isValid() else COLS

    def data(self, index, role=Qt.DisplayRole):
        return cell(index.row(), index.column()) if role == Qt.DisplayRole else None

    def headerData(self, s, o, role=Qt.DisplayRole):
        if role != Qt.DisplayRole:
            return None
        return f"C{s + 1}" if o == Qt.Horizontal else str(s + 1)


class Bridge(QObject):
    line = Signal(str)


def say(s):
    print(s, flush=True)


def main():
    app = QApplication(sys.argv)
    win = QMainWindow()
    win.setAttribute(Qt.WA_ShowWithoutActivating)
    win.setWindowFlag(Qt.Tool)  # keeps it off the taskbar
    win.setGeometry(-4000, -4000, 1280, 800)
    bar = QToolBar()
    bar.addAction("ملف")
    bar.addAction("بحث")
    win.addToolBar(bar)
    body = QWidget()
    lay = QHBoxLayout(body)
    side = QListWidget()
    side.addItems(["الوارد", "الجداول", "الملاحظات"])
    side.setFixedWidth(180)
    view = QTableView()
    view.verticalHeader().setDefaultSectionSize(22)
    view.setVerticalScrollMode(QAbstractItemView.ScrollPerItem)
    lay.addWidget(side)
    lay.addWidget(view)
    win.setCentralWidget(body)

    def table():
        t = time.perf_counter()
        view.setModel(Model(view))
        view.horizontalHeader().setDefaultSectionSize(110)
        view.grab()
        say(f"TABLE_READY {(time.perf_counter() - t) * 1000:.1f}")

    def timed(fn, n=200):
        out = []
        for i in range(n):
            t = time.perf_counter()
            fn(i)
            view.grab()
            out.append((time.perf_counter() - t) * 1000)
        out.sort()
        return out

    def bench():
        rng = random.Random(3)
        sb = view.verticalScrollBar()
        a = timed(lambda i: sb.setValue(rng.randint(0, ROWS - 40)))
        b = timed(lambda i: sb.setValue(i * 3))
        say(f"BENCH jump_med={a[100]:.2f} jump_p95={a[190]:.2f} wheel_med={b[100]:.2f} wheel_p95={b[190]:.2f}")

    def rtl(png):
        app.setLayoutDirection(Qt.RightToLeft)
        win.setLayoutDirection(Qt.RightToLeft)
        view.verticalScrollBar().setValue(0)
        win.grab().save(png)
        say("RTL_DONE")

    def handle(line):
        if line == "table":
            table()
        elif line == "bench":
            bench()
        elif line.startswith("rtl "):
            rtl(line[4:])
        elif line == "quit":
            app.quit()

    bridge = Bridge()
    bridge.line.connect(handle, Qt.QueuedConnection)

    def pump():
        for line in sys.stdin:
            bridge.line.emit(line.strip())
        bridge.line.emit("quit")  # the watcher is gone

    def ready():
        win.grab()
        say("READY")
        threading.Thread(target=pump, daemon=True).start()

    win.show()
    QTimer.singleShot(0, ready)
    return app.exec()


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stdin.reconfigure(encoding="utf-8")
    sys.exit(main())
