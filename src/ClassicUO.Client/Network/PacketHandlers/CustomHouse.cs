using System;
using ClassicUO.Game;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using ClassicUO.Game.Scenes;
using ClassicUO.Game.UI.Gumps;
using ClassicUO.IO;
using ClassicUO.Network.PacketHandlers.Helpers;
using ClassicUO.Utility.Logging;
using Microsoft.Xna.Framework;

namespace ClassicUO.Network.PacketHandlers;

internal static class CustomHouse
{
    public static void Receive(World world, ref StackDataReader p)
    {
        bool compressed = p.ReadUInt8() == 0x03;
        bool enableResponse = p.ReadBool();
        uint serial = p.ReadUInt32BE();
        Item foundation = world.Items.Get(serial);
        uint revision = p.ReadUInt32BE();

        Log.Info($"[CH-DEBUG] ==== 0xD8 received: serial=0x{serial:X8} revision={revision} compressed={compressed} enableResponse={enableResponse}");

        if (foundation == null)
        {
            Log.Info($"[CH-DEBUG] foundation item NOT FOUND for serial=0x{serial:X8} — returning");
            return;
        }

        Log.Info($"[CH-DEBUG] foundation: graphic=0x{foundation.Graphic:X4} ({foundation.Graphic}) pos=({foundation.X},{foundation.Y},{foundation.Z}) IsMulti={foundation.IsMulti} MultiInfo={(foundation.MultiInfo.HasValue ? $"X={foundation.MultiInfo.Value.X} Y={foundation.MultiInfo.Value.Y} W={foundation.MultiInfo.Value.Width} H={foundation.MultiInfo.Value.Height}" : "null")}");

        try
        {
            var fileMultis = Client.Game.UO.FileManager.Multis.GetMultis(foundation.Graphic);
            Log.Info($"[CH-DEBUG] file lookup GetMultis(0x{foundation.Graphic:X4}).Count={fileMultis.Count}");
            if (fileMultis.Count > 0)
            {
                int mnx = int.MaxValue, mny = int.MaxValue, mxx = int.MinValue, mxy = int.MinValue;
                foreach (var b in fileMultis)
                {
                    if (b.X < mnx) mnx = b.X;
                    if (b.X > mxx) mxx = b.X;
                    if (b.Y < mny) mny = b.Y;
                    if (b.Y > mxy) mxy = b.Y;
                }
                Log.Info($"[CH-DEBUG] file lookup bounds: minX={mnx} minY={mny} maxX={mxx} maxY={mxy}");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[CH-DEBUG] file lookup GetMultis threw: {ex}");
        }

        Rectangle? multi = foundation.MultiInfo;

        if (!foundation.IsMulti || multi == null)
        {
            Log.Info($"[CH-DEBUG] BAIL: !IsMulti || MultiInfo==null — TazUO does NOT parse this house's D8");
            return;
        }

        p.Skip(4);

        if (!world.HouseManager.TryGetHouse(foundation, out House house))
        {
            house = new House(world, foundation, revision, true);
            world.HouseManager.Add(foundation, house);
        }
        else
        {
            house.ClearComponents(true);
            house.Revision = revision;
            house.IsCustom = true;
        }

        short minX = (short)multi.Value.X;
        short minY = (short)multi.Value.Y;
        short maxY = (short)multi.Value.Height;

        if (minX == 0 && minY == 0 && maxY == 0 && multi.Value.Width == 0)
        {
            Log.Warn($"[CH-DEBUG] BAIL: multi dimensions all zero (minX={minX} minY={minY} maxY={maxY} W={multi.Value.Width}) — TazUO does NOT parse this house's D8");
            return;
        }

        byte planes = p.ReadUInt8();

        Log.Info($"[CH-DEBUG] decode bounds: minX={minX} minY={minY} maxY={maxY} (W={multi.Value.Width}); planes={planes}");

        house.ClearCustomHouseComponents(0);

        int countBeforeAll = house.Components.Count;

        for (int plane = 0; plane < planes; plane++)
        {
            uint header = p.ReadUInt32BE();
            int dlen = (int)(((header & 0xFF0000) >> 16) | ((header & 0xF0) << 4));
            int clen = (int)(((header & 0xFF00) >> 8) | ((header & 0x0F) << 8));
            int planeZ = (int)((header & 0x0F000000) >> 24);
            int planeMode = (int)((header & 0xF0000000) >> 28);

            Log.Info($"[CH-DEBUG] plane {plane}/{planes}: header=0x{header:X8} mode={planeMode} z={planeZ} dlen={dlen} clen={clen}");

            if (clen <= 0)
            {
                Log.Info($"[CH-DEBUG] plane {plane}: clen<=0, skipped");
                continue;
            }

            int countBefore = house.Components.Count;

            try
            {
                HouseHelpers.ReadUnsafeCustomHouseData(
                    p.Buffer,
                    p.Position,
                    dlen,
                    clen,
                    planeZ,
                    planeMode,
                    minX,
                    minY,
                    maxY,
                    foundation,
                    house
                );

                Log.Info($"[CH-DEBUG] plane {plane}: decoded OK, components added={house.Components.Count - countBefore}");
            }
            catch (Exception e)
            {
                Log.Error($"[CH-DEBUG] plane {plane}: FAILED to read custom house data: {e}");
            }

            p.Skip(clen);
        }

        int total = house.Components.Count;
        int custom = 0, noncustom = 0, c4a8 = 0;
        int bxMin = int.MaxValue, byMin = int.MaxValue, bxMax = int.MinValue, byMax = int.MinValue;
        int bzMin = int.MaxValue, bzMax = int.MinValue;

        foreach (var c in house.Components)
        {
            if (c.IsCustom) custom++; else noncustom++;
            if (c.Graphic == 0x4A8) c4a8++;
            if (c.X < bxMin) bxMin = c.X;
            if (c.X > bxMax) bxMax = c.X;
            if (c.Y < byMin) byMin = c.Y;
            if (c.Y > byMax) byMax = c.Y;
            if (c.Z < bzMin) bzMin = c.Z;
            if (c.Z > bzMax) bzMax = c.Z;
        }

        Log.Info($"[CH-DEBUG] decode complete: total={total} (addedByD8={total - countBeforeAll}) custom={custom} nonCustom={noncustom} graphic0x4A8count={c4a8}");

        if (total > 0)
            Log.Info($"[CH-DEBUG] component extent: x=[{bxMin}..{bxMax}] y=[{byMin}..{byMax}] z=[{bzMin}..{bzMax}]");

        foreach (var c in house.Components)
        {
            if (c.X == 1822 && c.Y == 2544)
                Log.Info($"[CH-DEBUG] TARGET (1822,2544): graphic=0x{c.Graphic:X4} ({c.Graphic}) z={c.Z} custom={c.IsCustom}");
        }

        if (world.CustomHouseManager != null)
        {
            world.CustomHouseManager.GenerateFloorPlace();

            UIManager.GetGump<HouseCustomizationGump>(house.Serial)?.Update();
        }

        UIManager.GetGump<MiniMapGump>()?.RequestUpdateContents();

        if (world.HouseManager.EntityIntoHouse(serial, world.Player))
            Client.Game.GetScene<GameScene>()?.UpdateMaxDrawZ(true);

        world.BoatMovingManager.ClearSteps(serial);
    }
}
