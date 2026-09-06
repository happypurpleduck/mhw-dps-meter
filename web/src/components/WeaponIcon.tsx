import { Show } from 'solid-js'
import { weaponDisplayName, weaponIconUrl } from '../data/names'

/** Compact class glyph; `title` carries the display name for hover. */
export function WeaponIcon(props: { weapon?: string | null; class?: string; size?: 'sm' | 'md' }) {
  const src = () => weaponIconUrl(props.weapon)
  const label = () => weaponDisplayName(props.weapon)
  const size = () => (props.size === 'sm' ? 'size-4' : 'size-5')
  return (
    <Show when={src()} fallback={<span class={['inline-block shrink-0', size(), props.class]} aria-hidden="true" />}>
      {(url) => (
        <img
          src={url()}
          alt=""
          title={label()}
          aria-label={label()}
          class={['inline-block shrink-0 object-contain', size(), props.class]}
        />
      )}
    </Show>
  )
}

/** Icon + label for tables where the weapon itself is the row identity. */
export function WeaponLabel(props: { weapon?: string | null; class?: string }) {
  return (
    <span class={['inline-flex items-center gap-1.5 min-w-0', props.class]}>
      <WeaponIcon weapon={props.weapon} />
      <span class="truncate">{weaponDisplayName(props.weapon)}</span>
    </span>
  )
}
