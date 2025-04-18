using Content.Shared.Atmos;
using Content.Shared.Radiation.Components;
using Content.Shared.Supermatter.Components;
using System.Text;
using Content.Shared.Chat;
using System.Linq;
using Content.Shared.Audio;
using Content.Shared.CCVar;

namespace Content.Server.Supermatter.Systems;

public sealed partial class SupermatterSystem
{
    /// <summary>
    ///     Handle power and radiation output depending on atmospheric things.
    /// </summary>
    private void ProcessAtmos(EntityUid uid, SupermatterComponent sm, float frameTime)
    {
        var mix = _atmosphere.GetContainingMixture(uid, true, true);

        if (mix is not { })
            return;

        var absorbedGas = mix.Remove(sm.GasEfficiency * mix.TotalMoles);
        var moles = absorbedGas.TotalMoles;

        if (!(moles > 0f))
            return;

        var gases = sm.GasStorage;
        var facts = sm.GasDataFields;

        // Lets get the proportions of the gasses in the mix for scaling stuff later
        // They range between 0 and 1
        gases = gases.ToDictionary(
            gas => gas.Key,
            gas => Math.Clamp(absorbedGas.GetMoles(gas.Key) / moles, 0, 1)
        );

        // No less then zero, and no greater then one, we use this to do explosions and heat to power transfer.
        var powerRatio = gases.Sum(gas => gases[gas.Key] * facts[gas.Key].PowerMixRatio);

        // Minimum value of -10, maximum value of 23. Affects plasma, o2 and heat output.
        var heatModifier = gases.Sum(gas => gases[gas.Key] * facts[gas.Key].HeatPenalty);

        // Minimum value of -10, maximum value of 23. Affects plasma, o2 and heat output.
        var transmissionBonus = gases.Sum(gas => gases[gas.Key] * facts[gas.Key].TransmitModifier);

        var h2OBonus = 1 - gases[Gas.WaterVapor] * 0.25f;

        powerRatio = Math.Clamp(powerRatio, 0, 1);
        heatModifier = Math.Max(heatModifier, 0.5f);
        transmissionBonus *= h2OBonus;

        // Effects the damage heat does to the crystal
        sm.DynamicHeatResistance = 1f;

        // More moles of gases are harder to heat than fewer, so let's scale heat damage around them
        sm.MoleHeatPenaltyThreshold = (float) Math.Max(moles * sm.MoleHeatPenalty, 0.25);

        // Ramps up or down in increments of 0.02 up to the proportion of CO2
        // Given infinite time, powerloss_dynamic_scaling = co2comp
        // Some value from 0-1
        if (moles > sm.PowerlossInhibitionMoleThreshold && gases[Gas.CarbonDioxide] > sm.PowerlossInhibitionGasThreshold)
        {
            var co2powerloss = Math.Clamp(gases[Gas.CarbonDioxide] - sm.PowerlossDynamicScaling, -0.02f, 0.02f);
            sm.PowerlossDynamicScaling = Math.Clamp(sm.PowerlossDynamicScaling + co2powerloss, 0f, 1f);
        }
        else
            sm.PowerlossDynamicScaling = Math.Clamp(sm.PowerlossDynamicScaling - 0.05f, 0f, 1f);

        // Ranges from 0~1(1 - (0~1 * 1~(1.5 * (mol / 500))))
        // We take the mol count, and scale it to be our inhibitor
        var powerlossInhibitor =
            Math.Clamp(
                1
                - sm.PowerlossDynamicScaling
                * Math.Clamp(
                    moles / sm.PowerlossInhibitionMoleBoostThreshold,
                    1f, 1.5f),
                0f, 1f);

        if (sm.MatterPower != 0) // We base our removed power off 1/10 the matter_power.
        {
            var removedMatter = Math.Max(sm.MatterPower / sm.MatterPowerConversion, 40);
            // Adds at least 40 power
            sm.Power = Math.Max(sm.Power + removedMatter, 0);
            // Removes at least 40 matter power
            sm.MatterPower = Math.Max(sm.MatterPower - removedMatter, 0);
        }

        // Based on gas mix, makes the power more based on heat or less effected by heat
        var tempFactor = powerRatio > 0.8 ? 50f : 30f;

        // If there is more pluox and N2 then anything else, we receive no power increase from heat
        sm.Power = Math.Max(absorbedGas.Temperature * tempFactor / Atmospherics.T0C * powerRatio + sm.Power, 0);

        // Irradiate stuff
        if (TryComp<RadiationSourceComponent>(uid, out var rad))
            rad.Intensity =
                sm.Power
                * Math.Max(0, 1f + transmissionBonus / 10f)
                * 0.003f
                * _config.GetCVar(CCVars.SupermatterRadsModifier);

        // Power * 0.55 * 0.8~1
        // This has to be differentiated with respect to time, since its going to be interacting with systems
        // that also differentiate. Basically, if we don't multiply by 2 * frameTime, the supermatter will explode faster if your server's tickrate is higher.
        var energy = 2 * sm.Power * sm.ReactionPowerModifier * frameTime;

        // Keep in mind we are only adding this temperature to (efficiency)% of the one tile the rock is on.
        // An increase of 4°C at 25% efficiency here results in an increase of 1°C / (#tilesincore) overall.
        // Power * 0.55 * 1.5~23 / 5
        absorbedGas.Temperature += energy * heatModifier * sm.ThermalReleaseModifier;
        absorbedGas.Temperature = Math.Max(0,
            Math.Min(absorbedGas.Temperature, sm.HeatThreshold * heatModifier));

        // Release the waste
        absorbedGas.AdjustMoles(Gas.Plasma, Math.Max(energy * heatModifier * sm.PlasmaReleaseModifier, 0f));
        absorbedGas.AdjustMoles(Gas.Oxygen, Math.Max((energy + absorbedGas.Temperature * heatModifier - Atmospherics.T0C) * sm.OxygenReleaseEfficiencyModifier, 0f));

        _atmosphere.Merge(mix, absorbedGas);

        var powerReduction = (float) Math.Pow(sm.Power / 500, 3);

        // After this point power is lowered
        // This wraps around to the begining of the function
        sm.Power = Math.Max(sm.Power - Math.Min(powerReduction * powerlossInhibitor, sm.Power * 0.83f * powerlossInhibitor), 0f);
    }

    /// <summary>
    ///     Shoot lightning bolts depensing on accumulated power.
    /// </summary>
    private void SupermatterZap(EntityUid uid, SupermatterComponent sm)
    {
        // Divide power by its' threshold to get a value from 0-1, then multiply by the amount of possible lightnings
        var zapPower = sm.Power / sm.PowerPenaltyThreshold * sm.LightningPrototypes.Length;
        var zapPowerNorm = (int) Math.Clamp(zapPower, 0, sm.LightningPrototypes.Length - 1);
        _lightning.ShootRandomLightnings(uid, 3.5f, sm.Power > sm.PowerPenaltyThreshold ? 3 : 1, sm.LightningPrototypes[zapPowerNorm]);
    }

    /// <summary>
    ///     Handles environmental damage.
    /// </summary>
    private void HandleDamage(EntityUid uid, SupermatterComponent sm)
    {
        var xform = Transform(uid);
        var indices = _xform.GetGridOrMapTilePosition(uid, xform);

        sm.DamageArchived = sm.Damage;

        var mix = _atmosphere.GetContainingMixture(uid, true, true);

        // We're in space or there is no gas to process
        if (!xform.GridUid.HasValue || mix is not { } || mix.TotalMoles == 0f)
        {
            sm.Damage += Math.Max(sm.Power / 1000 * sm.DamageIncreaseMultiplier, 0.1f);
            return;
        }

        // Absorbed gas from surrounding area
        var absorbedGas = mix.Remove(sm.GasEfficiency * mix.TotalMoles);
        var moles = absorbedGas.TotalMoles;

        var totalDamage = 0f;

        var tempThreshold = Atmospherics.T0C + sm.HeatPenaltyThreshold;

        // Temperature start to have a positive effect on damage after 350
        var tempDamage =
            Math.Max(
                Math.Clamp(moles / 200f, .5f, 1f)
                    * absorbedGas.Temperature
                    - tempThreshold
                    * sm.DynamicHeatResistance,
                0f)
                * sm.MoleHeatThreshold
                / 150f
                * sm.DamageIncreaseMultiplier;
        totalDamage += tempDamage;

        // Power only starts affecting damage when it is above 5000
        var powerDamage = Math.Max(sm.Power - sm.PowerPenaltyThreshold, 0f) / 500f * sm.DamageIncreaseMultiplier;
        totalDamage += powerDamage;

        // Mol count only starts affecting damage when it is above 1800
        var moleDamage = Math.Max(moles - sm.MolePenaltyThreshold, 0) / 80 * sm.DamageIncreaseMultiplier;
        totalDamage += moleDamage;

        // Healing damage
        if (moles < sm.MolePenaltyThreshold)
        {
            // There's a very small float so that it doesn't divide by 0
            var healHeatDamage = Math.Min(absorbedGas.Temperature - tempThreshold, 0.001f) / 150;
            totalDamage += healHeatDamage;
        }

        // Return the manipulated gas back to the mix
        _atmosphere.Merge(mix, absorbedGas);

        // Check for space tiles next to SM
        //TODO: Change moles out for checking if adjacent tiles exist
        var enumerator = _atmosphere.GetAdjacentTileMixtures(xform.GridUid.Value, indices, false, false);
        while (enumerator.MoveNext(out var ind))
        {
            if (ind.TotalMoles != 0)
                continue;

            var integrity = GetIntegrity(sm);

            var factor = integrity switch
            {
                < 10 => 0.0005f,
                < 25 => 0.0009f,
                < 45 => 0.005f,
                < 75 => 0.002f,
                _ => 0f
            };

            totalDamage += Math.Clamp(sm.Power * factor * sm.DamageIncreaseMultiplier, 0, sm.MaxSpaceExposureDamage);

            break;
        }

        var damage = Math.Min(sm.DamageArchived + sm.DamageHardcap * sm.DamageDelaminationPoint, totalDamage);

        // Prevent it from going negative
        sm.Damage = Math.Clamp(damage, 0, float.PositiveInfinity);
    }

    /// <summary>
    ///     Handles core damage announcements
    /// </summary>
    private void AnnounceCoreDamage(EntityUid uid, SupermatterComponent sm)
    {
        var message = string.Empty;
        var global = false;

        var integrity = GetIntegrity(sm).ToString("0.00");

        // Special cases
        if (sm.Damage < sm.DamageDelaminationPoint && sm.Delamming)
        {
            message = Loc.GetString("supermatter-delam-cancel", ("integrity", integrity));
            sm.DelamAnnounced = false;
            global = true;
        }

        if (sm.Delamming && !sm.DelamAnnounced)
        {
            var sb = new StringBuilder();
            var loc = string.Empty;

            switch (sm.PreferredDelamType)
            {
                case DelamType.Cascade: loc = "supermatter-delam-cascade";   break;
                case DelamType.Singulo: loc = "supermatter-delam-overmass";  break;
                case DelamType.Tesla:   loc = "supermatter-delam-tesla";     break;
                default:                loc = "supermatter-delam-explosion"; break;
            }

            var station = _station.GetOwningStation(uid);
            if (station != null)
                _alert.SetLevel((EntityUid) station, sm.AlertCodeDeltaId, true, true, true, false);

            sb.AppendLine(Loc.GetString(loc));
            sb.AppendLine(Loc.GetString("supermatter-seconds-before-delam", ("seconds", sm.DelamTimer)));

            message = sb.ToString();
            global = true;
            sm.DelamAnnounced = true;

            SendSupermatterAnnouncement(uid, message, global);
            return;
        }

        // Ignore the 0% integrity alarm
        if (sm.Delamming)
            return;

        // We are not taking consistent damage, Engineers aren't needed
        if (sm.Damage <= sm.DamageArchived)
            return;

        if (sm.Damage >= sm.DamageWarningThreshold)
        {
            message = Loc.GetString("supermatter-warning", ("integrity", integrity));
            if (sm.Damage >= sm.DamageEmergencyThreshold)
            {
                message = Loc.GetString("supermatter-emergency", ("integrity", integrity));
                global = true;
            }
        }

        SendSupermatterAnnouncement(uid, message, global);
    }

    /// <param name="global">If true, sends a station announcement</param>
    /// <param name="customSender">Localisation string for a custom announcer name</param>
    public void SendSupermatterAnnouncement(EntityUid uid, string message, bool global = false, string? customSender = null)
    {
        if (global)
        {
            var sender = Loc.GetString(customSender != null ? customSender : "supermatter-announcer");
            _chat.DispatchStationAnnouncement(uid, message, sender, colorOverride: Color.Yellow);
            return;
        }

        _chat.TrySendInGameICMessage(uid, message, InGameICChatType.Speak, hideChat: false, checkRadioPrefix: true);
    }

    /// <summary>
    ///     Returns the integrity rounded to hundreds, e.g. 100.00%
    /// </summary>
    public float GetIntegrity(SupermatterComponent sm)
    {
        var integrity = sm.Damage / sm.DamageDelaminationPoint;
        integrity = (float) Math.Round(100 - integrity * 100, 2);
        integrity = integrity < 0 ? 0 : integrity;
        return integrity;
    }

    /// <summary>
    ///     Decide on how to delaminate.
    /// </summary>
    public DelamType ChooseDelamType(EntityUid uid, SupermatterComponent sm)
    {
        if (_config.GetCVar(CCVars.SupermatterDoForceDelam))
            return _config.GetCVar(CCVars.SupermatterForcedDelamType);

        var mix = _atmosphere.GetContainingMixture(uid, true, true);

        if (mix is { })
        {
            var absorbedGas = mix.Remove(sm.GasEfficiency * mix.TotalMoles);
            var moles = absorbedGas.TotalMoles;

            if (_config.GetCVar(CCVars.SupermatterDoSingulooseDelam)
                && moles >= sm.MolePenaltyThreshold * _config.GetCVar(CCVars.SupermatterSingulooseMolesModifier))
                return DelamType.Singulo;
        }

        if (_config.GetCVar(CCVars.SupermatterDoTeslooseDelam)
            && sm.Power >= sm.PowerPenaltyThreshold * _config.GetCVar(CCVars.SupermatterTesloosePowerModifier))
            return DelamType.Tesla;

        //TODO: Add resonance cascade when there's crazy conditions or a destabilizing crystal

        return DelamType.Explosion;
    }

    /// <summary>
    ///     Handle the end of the station.
    /// </summary>
    private void HandleDelamination(EntityUid uid, SupermatterComponent sm)
    {
        var xform = Transform(uid);

        sm.PreferredDelamType = ChooseDelamType(uid, sm);

        if (!sm.Delamming)
        {
            sm.Delamming = true;
            AnnounceCoreDamage(uid, sm);
        }

        if (sm.Damage < sm.DamageDelaminationPoint && sm.Delamming)
        {
            sm.Delamming = false;
            AnnounceCoreDamage(uid, sm);
        }

        sm.DelamTimerAccumulator++;

        if (sm.DelamTimerAccumulator < sm.DelamTimer)
            return;

        switch (sm.PreferredDelamType)
        {
            case DelamType.Cascade:
                Spawn(sm.KudzuSpawnPrototype, xform.Coordinates);
                break;

            case DelamType.Singulo:
                Spawn(sm.SingularitySpawnPrototype, xform.Coordinates);
                break;

            case DelamType.Tesla:
                Spawn(sm.TeslaSpawnPrototype, xform.Coordinates);
                break;

            default:
                _explosion.TriggerExplosive(uid);
                break;
        }
    }

    /// <summary>
    ///     Swaps out ambience sounds when the SM is delamming or not.
    /// </summary>
    private void HandleSoundLoop(EntityUid uid, SupermatterComponent sm)
    {
        var ambient = Comp<AmbientSoundComponent>(uid);

        if (ambient == null)
            return;

        if (sm.Delamming && sm.CurrentSoundLoop != sm.DelamSound)
            sm.CurrentSoundLoop = sm.DelamSound;

        else if (!sm.Delamming && sm.CurrentSoundLoop != sm.CalmSound)
            sm.CurrentSoundLoop = sm.CalmSound;

        if (ambient.Sound != sm.CurrentSoundLoop)
            _ambient.SetSound(uid, sm.CurrentSoundLoop, ambient);
    }
}
