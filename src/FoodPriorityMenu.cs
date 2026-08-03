using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;
using StardewValley.BellsAndWhistles;
using StardewValley.Menus;
using Object = StardewValley.Object;

namespace AutoEatLowHealth;

/// <summary>进食优先级设置界面(原版实现保留,仅清理编译问题)。</summary>
public class FoodPriorityMenu : IClickableMenu
{
    private class FoodIcon
    {
        public string Qid = "";
        public Item? Item;
        public Rectangle Bounds;
    }

    private readonly ModConfig Config;
    private readonly List<FoodIcon> Candidates = new();
    private readonly List<FoodIcon> Selected = new();

    public FoodPriorityMenu(ModConfig config)
        : base(Game1.uiViewport.Width / 2 - (800 + IClickableMenu.borderWidth * 2) / 2,
               Game1.uiViewport.Height / 2 - (600 + IClickableMenu.borderWidth * 2) / 2,
               800 + IClickableMenu.borderWidth * 2,
               600 + IClickableMenu.borderWidth * 2,
               showUpperRightCloseButton: true)
    {
        Config = config;
        BuildIcons();
    }

    private void BuildIcons()
    {
        Candidates.Clear();
        Selected.Clear();
        var seen = new List<string>();
        foreach (Item item in Game1.player.Items)
        {
            if (item is Object obj && obj.Edibility >= 0 && !seen.Contains(item.QualifiedItemId))
                seen.Add(item.QualifiedItemId);
        }

        int startX = xPositionOnScreen + 90;
        int x = startX;
        int y = yPositionOnScreen + 180;
        foreach (string qid in seen)
        {
            Candidates.Add(new FoodIcon { Qid = qid, Item = Make(qid), Bounds = new Rectangle(x, y, 64, 64) });
            x += 72;
            if (x > xPositionOnScreen + width - 110)
            {
                x = startX;
                y += 72;
            }
        }

        int sx = xPositionOnScreen + 90;
        int sy = yPositionOnScreen + height - 190;
        foreach (string qid in Config.FoodPriority)
        {
            Selected.Add(new FoodIcon { Qid = qid, Item = Make(qid), Bounds = new Rectangle(sx, sy, 64, 64) });
            sx += 72;
        }
    }

    private static Item? Make(string qid)
    {
        try
        {
            return ItemRegistry.Create(qid, 1, 0, false);
        }
        catch
        {
            return null;
        }
    }

    public override void receiveLeftClick(int x, int y, bool playSound = true)
    {
        base.receiveLeftClick(x, y, playSound);

        foreach (FoodIcon candidate in Candidates)
        {
            if (candidate.Bounds.Contains(x, y))
            {
                Game1.playSound("smallSelect");
                if (!Config.FoodPriority.Contains(candidate.Qid))
                    Config.FoodPriority.Add(candidate.Qid);
                BuildIcons();
                return;
            }
        }
        foreach (FoodIcon item in Selected)
        {
            if (item.Bounds.Contains(x, y))
            {
                Game1.playSound("bigDeSelect");
                Config.FoodPriority.Remove(item.Qid);
                BuildIcons();
                return;
            }
        }
        if (CloseButtonRect().Contains(x, y))
        {
            Game1.playSound("bigDeSelect");
            SaveAndExit();
        }
    }

    private Rectangle CloseButtonRect() => new(xPositionOnScreen + width - 220, yPositionOnScreen + height - 90, 180, 60);

    private void SaveAndExit()
    {
        ModEntry.Instance.Helper.WriteConfig(Config);
        exitThisMenu();
    }

    public override void draw(SpriteBatch b)
    {
        b.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.75f);
        Game1.drawDialogueBox(xPositionOnScreen, yPositionOnScreen, width, height, false, true);

        SpriteText.drawStringHorizontallyCenteredAt(b, "进食优先级", xPositionOnScreen + width / 2, yPositionOnScreen + 60);
        SpriteText.drawString(b, "背包里能吃的(点图标加入):", xPositionOnScreen + 90, yPositionOnScreen + 130);
        if (Candidates.Count == 0)
        {
            SpriteText.drawString(b, "(背包里现在没有能吃的东西)", xPositionOnScreen + 90, yPositionOnScreen + 200);
        }
        else
        {
            foreach (FoodIcon candidate in Candidates)
                DrawIcon(b, candidate, Config.FoodPriority.Contains(candidate.Qid));
        }

        SpriteText.drawString(b, "优先级顺序(越靠前越先吃,点可移除):", xPositionOnScreen + 90, yPositionOnScreen + height - 240);
        if (Selected.Count == 0)
        {
            SpriteText.drawString(b, "(未设置 → 吃背包里第一个能吃的)", xPositionOnScreen + 90, yPositionOnScreen + height - 180);
        }
        else
        {
            int num = 1;
            foreach (FoodIcon item in Selected)
            {
                DrawIcon(b, item, alreadyChosen: false);
                SpriteText.drawString(b, num.ToString(), item.Bounds.X - 2, item.Bounds.Y - 6);
                num++;
            }
        }

        Rectangle close = CloseButtonRect();
        IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(384, 373, 18, 18), close.X, close.Y, close.Width, close.Height, Color.White, 4f, true);
        SpriteText.drawString(b, "完成", close.X + 60, close.Y + 12);
        drawMouse(b);
    }

    private void DrawIcon(SpriteBatch b, FoodIcon icon, bool alreadyChosen)
    {
        IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(384, 373, 18, 18), icon.Bounds.X, icon.Bounds.Y, icon.Bounds.Width, icon.Bounds.Height, alreadyChosen ? Color.Gray * 0.5f : Color.White, 4f, true);
        icon.Item?.drawInMenu(b, new Vector2(icon.Bounds.X + 8, icon.Bounds.Y + 8), 1f, alreadyChosen ? 0.5f : 1f, 1f);
    }
}
