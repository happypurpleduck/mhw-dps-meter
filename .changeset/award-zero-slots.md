---
"mhw-dps-meter": patch
---

Keep every slot on synchronized quest-award totals once party awards are available, including local zero values. Mixing raw hook damage into a zero award slot could inflate the overlay and saved history, then make cumulative damage fall when that slot received its first positive award.
