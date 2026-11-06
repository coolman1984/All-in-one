"""Makes a made-up 200-page PDF for the Windows PDF probe. Uses PyMuPDF, already on this PC. Usage: python make_pdf.py <out.pdf>"""
import sys

import fitz

doc = fitz.open()
for i in range(200):
    p = doc.new_page()
    p.insert_text((72, 72), f"Made-up report, page {i + 1}", fontsize=20)
    for k in range(40):
        p.insert_text((72, 110 + k * 16), f"Row {k + 1}: item {i * 40 + k}, quantity {(i * 7 + k * 3) % 991}, status checked", fontsize=11)
    p.draw_rect(fitz.Rect(72, 760, 300, 790), color=(0.2, 0.3, 0.8))
doc.save(sys.argv[1])
print("pages", doc.page_count)
