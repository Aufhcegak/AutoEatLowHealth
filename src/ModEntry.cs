using HarmonyLib;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;
using StardewValley.Monsters;
using Object = StardewValley.Object;

namespace AutoEatLowHealth;

/// <summary>
/// 濒死自动进食。血量低于阈值时弹出进食询问,选择期间无敌。
///
/// 2026-08-04 联机修复(对照原版源码):
/// 1. 【对话阶段弹不出/无无敌帧】CanPromptNow 原实现要求 activeClickableMenu==null ——
///    对话(DialogueBox)开着时永远 false → 不弹窗、不设无敌帧 → 对话期间被打死。
///    修复:仅当【自己 mod 的提示】或【会挡输入的非对话菜单】打开时才拒绝弹窗;
///    对话(含问答题)允许弹窗 —— 且弹窗用 createQuestionDialogue 会被已有对话排队,
///    改为用 Game1.activeClickableMenu 直接开 ItemGrabMenu 风格的专属选择界面?
///    不 —— 对话阶段最稳妥是【自动吃,不弹窗】:受击时直接按优先级吃,弹窗只留给无菜单时。
///    同时无敌帧从"弹窗期间"改为"受击触发后到吃完"全程保持(含对话阶段)。
/// 2. 【吃完还在/回不上血】DoEat 原实现 eatObject(getOne(), false) ——
///    原版 eatObject(Farmer.cs:9138) hasBuff("6") && !overrideFullness 时拒绝进食
///    (饱食 buff 在时 HUD 提示"吃不下了",食物已扣) → "吃完还在"。
///    修复:eatObject(o, overrideFullness: true) —— 濒死时无条件进食。
/// 3. 【无敌帧时长】受击触发设置 currentTemporaryInvincibilityDuration = 100000(玩家帧 100 秒),
///    原版 Farmer.cs:8478 倒计时走完自动清除。修复:统一 3000ms 足够吃完,弹窗/进食结束显式清除。
/// 4. 【房主/访客一致】takeDamage prefix 在每端本地执行(伤害 NetEvent 每端广播),
///    无敌帧/进食都是本地玩家操作,天然一致。修复只保证两端行为相同(原实现已是对称的,
///    但 CanPromptNow 的菜单限制和 eatObject 参数两端口径统一修正)。
/// </summary>
public sealed class ModEntry : Mod
{
    internal static ModEntry Instance = null!;
    internal ModConfig Config = null!;
    private IGenericModConfigMenuApi? Gmcm;

    /// <summary>提示是否已打开(无敌帧维持中)。</summary>
    internal bool PromptOpen;

    private double LastPromptTime = -999.0;

    /// <summary>受击触发的无敌帧持续时间(毫秒,玩家帧)。足够吃完一轮。</summary>
    private const int InvincibleMs = 3000;

    public override void Entry(IModHelper helper)
    {
        Instance = this;
        Config = helper.ReadConfig<ModConfig>();

        var harmony = new Harmony(ModManifest.UniqueID);
        harmony.PatchAll();

        helper.Events.GameLoop.GameLaunched += OnGameLaunched;
        helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
        helper.Events.GameLoop.SaveLoaded += (_, _) =>
        {
            PromptOpen = false;
            LastPromptTime = -999.0;
        };

        helper.ConsoleCommands.Add("eat_priority", "打开濒死自动进食的优先级设置界面。", (_, _) =>
        {
            if (!Context.IsWorldReady)
            {
                Monitor.Log("请先进入存档。", LogLevel.Warn);
                return;
            }
            Game1.activeClickableMenu = new FoodPriorityMenu(Config);
        });
    }

    private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
    {
        Gmcm = Helper.ModRegistry.GetApi<IGenericModConfigMenuApi>("spacechase0.GenericModConfigMenu");
        if (Gmcm != null)
            RegisterGmcm();
    }

    /// <summary>
    /// 能否触发弹窗/自动吃。
    /// 联机修复:对话(DialogueBox)不拦 —— 对话阶段也要能触发(否则濒死时在对话里被打死)。
    /// 只拦"会吃掉输入的独占菜单"(背包/商店等) —— 那些菜单下玩家自己在操作,不该强弹。
    /// 自己 mod 的提示已开时不重复弹。
    /// </summary>
    internal bool CanPromptNow()
    {
        if (!Context.IsWorldReady || PromptOpen)
            return false;
        if (Game1.activeClickableMenu != null && Game1.activeClickableMenu is not DialogueBox)
            return false;   // 独占菜单(背包/商店/日记等):不弹,让玩家自己处理
        double now = Game1.currentGameTime?.TotalGameTime.TotalSeconds ?? 0.0;
        return now - LastPromptTime >= Config.CooldownSeconds;
    }

    internal void MarkPrompted()
    {
        LastPromptTime = Game1.currentGameTime?.TotalGameTime.TotalSeconds ?? 0.0;
    }

    /// <summary>受击触发:设置无敌帧 + 弹窗(无菜单时)或自动吃(对话/菜单中)。</summary>
    internal void OnHitLowHealth(Farmer who)
    {
        if (who == null || !who.IsLocalPlayer)
            return;

        // 无敌帧立即开(对话阶段也生效)
        SetInvincible();

        if (Game1.activeClickableMenu is DialogueBox)
        {
            // 对话阶段:弹窗会被排队/不显示 → 直接自动吃(按优先级)。
            // 吃不到就保持无敌帧(玩家退出对话后自己处理)。
            EatByPriority();
            PromptOpen = false;
        }
        else if (Game1.activeClickableMenu == null)
        {
            PromptOpen = true;
            OpenPrompt();
        }
        else
        {
            // 独占菜单(背包等):不开无敌弹窗,保持无敌帧让玩家自己吃
            PromptOpen = false;
        }
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        if (!Context.IsWorldReady)
            return;
        // 提示/进食期间维持无敌帧(原版 Farmer.cs 倒计时自动清除,这里持续刷新直到 ClearInvincible)
        if (PromptOpen && Config.InvincibleWhilePrompt)
        {
            Game1.player.temporarilyInvincible = true;
            Game1.player.temporaryInvincibilityTimer = 0;
            Game1.player.currentTemporaryInvincibilityDuration = InvincibleMs;
        }
    }

    private void SetInvincible()
    {
        if (!Config.InvincibleWhilePrompt)
            return;
        Game1.player.temporarilyInvincible = true;
        Game1.player.temporaryInvincibilityTimer = 0;
        Game1.player.currentTemporaryInvincibilityDuration = InvincibleMs;
    }

    private void OpenPrompt()
    {
        var responses = new List<Response>
        {
            new("yes", "吃!(按优先级)"),
            new("other", "我要吃别的..."),
            new("no", "不吃")
        };
        Game1.currentLocation?.createQuestionDialogue("血量危险了!要吃点东西吗?", responses.ToArray(), OnAnswer, null);
    }

    private void OnAnswer(Farmer who, string answer)
    {
        PromptOpen = false;
        switch (answer)
        {
            case "yes":
                EatByPriority();
                break;
            case "other":
                OpenPickOther();
                break;
            default:
                ClearInvincible();
                break;
        }
    }

    internal void ClearInvincible()
    {
        if (!Config.InvincibleWhilePrompt)
            return;
        Game1.player.temporarilyInvincible = false;
        Game1.player.temporaryInvincibilityTimer = 0;
    }

    private void EatByPriority()
    {
        Object? food = PickFood();
        if (food == null)
        {
            Game1.addHUDMessage(new HUDMessage("背包里没有能吃的东西!", HUDMessage.error_type));
        }
        else
        {
            DoEat(food);
        }
    }

    private Object? PickFood()
    {
        var list = Game1.player.Items.Where(IsEdible).Cast<Object>().ToList();
        if (list.Count == 0)
            return null;
        foreach (string wantId in Config.FoodPriority)
        {
            var match = list.FirstOrDefault(f => f.QualifiedItemId == wantId);
            if (match != null)
                return match;
        }
        return list[0];
    }

    /// <summary>
    /// 吃食物。联机修复:eatObject 必须 overrideFullness:true ——
    /// 原版 Farmer.cs:9138 hasBuff("6") && !overrideFullness 时拒绝进食(饱食 buff 在,
    /// HUD 提示"吃不下了",但调用方已扣食物) → "吃完还在/回不上血"。濒死时无条件进食。
    /// </summary>
    private void DoEat(Object food)
    {
        if (food.Stack > 1)
            food.Stack--;
        else
            Game1.player.Items.Remove(food);

        // ⚠️ 关键:overrideFullness=true —— 濒死时无视饱食 buff 强制进食
        Game1.player.eatObject(food.getOne() as Object ?? food, overrideFullness: true);
        ClearInvincible();
        Game1.addHUDMessage(new HUDMessage("吃掉了 " + food.DisplayName, HUDMessage.achievement_type));
    }

    private void OpenPickOther()
    {
        Game1.activeClickableMenu = new ItemGrabMenu(
            Game1.player.Items,
            reverseGrab: false,
            showReceivingMenu: false,
            IsEdible,
            (chosen, _) =>
            {
                if (chosen is Object obj && IsEdible(obj))
                    DoEat(obj);
            },
            "选择一样要吃的东西",
            null,
            snapToBottom: false,
            canBeExitedWithKey: true,
            playRightClickSound: true,
            allowRightClick: true,
            showOrganizeButton: true,
            source: 0,
            sourceItem: null,
            whichSpecialButton: -1,
            context: null);
    }

    private bool IsEdible(Item item)
    {
        return item is Object obj && obj.Edibility >= 0 && obj.QualifiedItemId != "(O)447";
    }

    private void RegisterGmcm()
    {
        if (Gmcm == null) return;
        Gmcm.Register(ModManifest,
            reset: () => Config = new ModConfig(),
            save: () => Helper.WriteConfig(Config));
        Gmcm.AddSectionTitle(ModManifest, () => "濒死自动进食");
        Gmcm.AddNumberOption(ModManifest,
            getValue: () => Config.HealthThreshold,
            setValue: v => Config.HealthThreshold = v,
            name: () => "触发血量阈值",
            tooltip: () => "血量低于该比例时触发(0.2 = 20%,0.5 = 50%)。",
            min: 0.2f, max: 0.5f, interval: 0.05f);
        Gmcm.AddBoolOption(ModManifest,
            getValue: () => Config.InvincibleWhilePrompt,
            setValue: v => Config.InvincibleWhilePrompt = v,
            name: () => "选择期间无敌",
            tooltip: () => "触发后/进食期间进入无敌帧(对话中同样生效)。",
            fieldId: "invincible");
        Gmcm.AddNumberOption(ModManifest,
            getValue: () => Config.CooldownSeconds,
            setValue: v => Config.CooldownSeconds = v,
            name: () => "触发冷却(秒)",
            tooltip: () => "两次触发的最短间隔,避免被围殴时反复触发。",
            min: 0, max: 60, interval: 1);
        Gmcm.AddSectionTitle(ModManifest, () => "进食优先级");
        Gmcm.AddParagraph(ModManifest, () => "在 SMAPI 控制台输入命令 eat_priority 打开优先级设置:列出背包里所有能吃的,点图标加入,排列顺序即优先级(越靠前越先吃)。");
    }
}

/// <summary>受击前缀:血量低于阈值 → 触发进食 + 无敌帧(联机每端本地执行,房主访客一致)。</summary>
[HarmonyPatch(typeof(Farmer), "takeDamage")]
internal static class TakeDamagePatch
{
    private static bool Prefix(Farmer __instance, int damage, Monster? damager)
    {
        try
        {
            ModEntry mod = ModEntry.Instance;
            if (mod == null || !mod.Config.InvincibleWhilePrompt)
                return true;
            if (!__instance.IsLocalPlayer || mod.PromptOpen)
                return true;
            // 无敌帧已开且不是本 mod 触发 → 放行原版(原版无敌帧照常工作)
            if (__instance.temporarilyInvincible)
                return true;
            int maxHealth = __instance.maxHealth;
            if (maxHealth <= 0)
                return true;
            float ratio = (float)(__instance.health - damage) / maxHealth;
            if (ratio >= mod.Config.HealthThreshold)
                return true;
            if (!mod.CanPromptNow())
                return true;

            mod.MarkPrompted();
            // 延迟一帧触发(等本次伤害结算完成,避免递归/竞态)
            DelayedAction.functionAfterDelay(() => mod.OnHitLowHealth(__instance), 50);
            // 拦截本次伤害(濒死一击直接挡下)
            return false;
        }
        catch (Exception)
        {
            return true;   // 异常放行原版,绝不拖垮玩家
        }
    }
}
