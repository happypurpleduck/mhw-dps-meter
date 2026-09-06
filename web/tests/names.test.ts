import { describe, expect, it } from 'vitest'
import names from '../../data/game-names.json'
import { stageDisplayName, weaponDisplayName } from '../src/data/names'
import { moveDisplayName } from '../src/moves/names'

describe('game display names', () => {
  it('covers every playable weapon without changing lookup keys', () => {
    expect(Object.keys(names.weapons).filter((id) => id !== '255')).toHaveLength(14)
    expect(weaponDisplayName('SwordAndShield')).toBe('Sword & Shield')
    expect(weaponDisplayName('GunLance')).toBe('Gunlance')
    expect(moveDisplayName('DualBlades', 'WP_02::RANBU')).toBe('Blade Dance')
    expect(weaponDisplayName('FutureWeapon')).toBe('FutureWeapon')
    expect(weaponDisplayName(null)).toBe('Unknown weapon')
  })
  it('maps playable weapons to icon URLs and skips unknown keys', async () => {
    const { weaponIconUrl } = await import('../src/data/names')
    expect(weaponIconUrl('DualBlades')).toMatch(/weapon-icons\/DualBlades\.svg$/)
    expect(weaponIconUrl('None')).toBeNull()
    expect(weaponIconUrl('FutureWeapon')).toBeNull()
    expect(weaponIconUrl(null)).toBeNull()
  })
  it('repairs raw or absent stage names and preserves localized names', () => {
    expect(stageDisplayName(416, 'AlatreonStage')).toBe('Secluded Valley')
    expect(stageDisplayName(105)).toBe("Elder's Recess")
    expect(stageDisplayName(504, '504')).toBe('Training Area')
    expect(stageDisplayName(101, 'Forêt ancienne')).toBe('Forêt ancienne')
    expect(stageDisplayName(999)).toBe('Stage 999')
    expect(stageDisplayName(999, 'Custom map')).toBe('Custom map')
  })
})
