use std::sync::OnceLock;
use serde_json::Value;

fn names() -> &'static Value {
    static NAMES: OnceLock<Value> = OnceLock::new();
    NAMES.get_or_init(|| serde_json::from_str(include_str!("../../data/game-names.json")).expect("valid game name table"))
}

pub fn weapon_display_name(key: Option<&str>) -> String {
    let Some(key) = key.filter(|key| !key.is_empty()) else { return "Unknown weapon".into(); };
    names()["weapons"].as_object().unwrap().values()
        .find(|entry| entry["key"].as_str() == Some(key))
        .and_then(|entry| entry["name"].as_str()).unwrap_or(key).into()
}

/// Embedded SVG for a playable weapon class, keyed by SPL `WeaponType` name.
pub fn weapon_icon_svg(key: Option<&str>) -> Option<&'static [u8]> {
    match key? {
        "GreatSword" => Some(include_bytes!("../../data/weapon-icons/GreatSword.svg")),
        "SwordAndShield" => Some(include_bytes!("../../data/weapon-icons/SwordAndShield.svg")),
        "DualBlades" => Some(include_bytes!("../../data/weapon-icons/DualBlades.svg")),
        "LongSword" => Some(include_bytes!("../../data/weapon-icons/LongSword.svg")),
        "Hammer" => Some(include_bytes!("../../data/weapon-icons/Hammer.svg")),
        "HuntingHorn" => Some(include_bytes!("../../data/weapon-icons/HuntingHorn.svg")),
        "Lance" => Some(include_bytes!("../../data/weapon-icons/Lance.svg")),
        "GunLance" => Some(include_bytes!("../../data/weapon-icons/GunLance.svg")),
        "SwitchAxe" => Some(include_bytes!("../../data/weapon-icons/SwitchAxe.svg")),
        "ChargeBlade" => Some(include_bytes!("../../data/weapon-icons/ChargeBlade.svg")),
        "InsectGlaive" => Some(include_bytes!("../../data/weapon-icons/InsectGlaive.svg")),
        "Bow" => Some(include_bytes!("../../data/weapon-icons/Bow.svg")),
        "LightBowgun" => Some(include_bytes!("../../data/weapon-icons/LightBowgun.svg")),
        "HeavyBowgun" => Some(include_bytes!("../../data/weapon-icons/HeavyBowgun.svg")),
        _ => None,
    }
}

pub fn stage_display_name(id: i64, recorded: Option<&str>) -> String {
    let entry = &names()["stages"][id.to_string()];
    if let Some(recorded) = recorded.filter(|s| !s.trim().is_empty()) {
        if Some(recorded) != entry["key"].as_str() && recorded != id.to_string() {
            return recorded.into();
        }
    }
    entry["name"].as_str().map(str::to_owned).unwrap_or_else(|| format!("Stage {id}"))
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn labels_preserve_unknown_and_localized_names() {
        assert_eq!(weapon_display_name(Some("GunLance")), "Gunlance");
        assert_eq!(weapon_display_name(Some("FutureWeapon")), "FutureWeapon");
        assert_eq!(stage_display_name(416, Some("AlatreonStage")), "Secluded Valley");
        assert_eq!(stage_display_name(101, Some("Forêt ancienne")), "Forêt ancienne");
        assert_eq!(stage_display_name(999, None), "Stage 999");
    }

    #[test]
    fn every_playable_weapon_has_an_icon() {
        for key in [
            "GreatSword", "SwordAndShield", "DualBlades", "LongSword", "Hammer", "HuntingHorn",
            "Lance", "GunLance", "SwitchAxe", "ChargeBlade", "InsectGlaive", "Bow", "LightBowgun", "HeavyBowgun",
        ] {
            assert!(weapon_icon_svg(Some(key)).is_some_and(|bytes| bytes.starts_with(b"<svg")), "{key}");
        }
        assert!(weapon_icon_svg(Some("None")).is_none());
        assert!(weapon_icon_svg(None).is_none());
    }
}
