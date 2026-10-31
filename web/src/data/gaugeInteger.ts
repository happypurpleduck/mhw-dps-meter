/** Plugin schema-3 gauge IDs and counters are signed 32-bit integers (GPUI mirrors i32).
 * Reject malformed optional values without fabricating a recorded zero.
 */
export function isGaugeI32(value: unknown): value is number {
  return typeof value === 'number' && Number.isInteger(value)
    && value >= -2147483648 && value <= 2147483647
}

export function gaugeI32(value: unknown): number | null {
  return isGaugeI32(value) ? value : null
}

/** A recorded proc/flinch counter can be observed only when nonnegative. */
export function isObservedGaugeCounter(value: unknown): value is number {
  return isGaugeI32(value) && value >= 0
}
