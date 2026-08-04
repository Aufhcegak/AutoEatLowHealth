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
/// 1. 【无敌的联机一致性】原实现手动刷 temporarilyInvincible(本地字段,联机不同步)
///    → 访客端无敌帧不同步/对话期间被打死。
///    修复:改为原版 Buff 机制 —— applyBuff 后 AppliedBuffIds 走 NetField 同步,
///    配合 CanBeDamaged 前缀(照抄原版雅巴戒指 hasBuff("21") 模式)拦截全部伤害来源。
///    进食动画期间的原版 isEating 无敌(CanBeDamaged 内含 !isEating)自动接管,无需自己维护。
/// 2. 【吃完还在/回不上血】原实现 eatObject(getOne(), false) ——
///    原版 Farmer.cs:9138 hasBuff("6") && !overrideFullness 时拒绝进食
///    (饱食 buff 在时 HUD 提示"吃不下了",食物已扣) → "吃完还在"。
///    修复:eatObject(o, overrideFullness: true) —— 濒死时无条件进食。
/// 3. 【贴脸 0 血必死】触发判断原来在 takeDamage 前缀用裸 damage 算 ——
///    原版真实伤害要经防御/随机减免,裸 damage 对高防玩家误判"会死"。
///    修复:前缀只拦【必死】(裸 damage ≥ 当前血 —— 减伤只可能让伤害更低,必死判断成立),
///    低血触发挪到 Postfix(真实扣血后判定)。
/// 4. 【状态机】保护期(Protecting)内全程刷新 buff(弹窗/自选菜单/等待中都不空窗);
///    保护结束走唯一出口 EndProtection(吃完 / 回答不吃 / ESC 关弹窗 / 关自选菜单 / 血回阈值上)。
///    弹窗受冷却约束防轰炸;必死拦截不受冷却约束(保命优先),冷却一过由心跳补弹窗。
/// 5. 【房主/访客一致】伤害计算(health NetInt 属主)由各玩家本地端结算,Buff 状态 Net 同步 —— 两端行为一致。
/// </summary>
public sealed class ModEntry : Mod
{
    internal static ModEntry Instance = null!;
    internal ModConfig Config = null!;
    private IGenericModConfigMenuApi? Gmcm;

    /// <summary>提问弹窗是否开着(保护 buff 维持中)。</summary>
    internal bool PromptOpen;

    /// <summary>保护期:已进入保护流程,需维持 buff。DoEat/EndProtection 时清除。</summary>
    internal bool Protecting;

    /// <summary>"我要吃别的..." 自选菜单是否开着(关掉时结束保护)。</summary>
    private bool PickOtherOpen;

    /// <summary>本周期已触发(延迟回调待执行),防重复。回调执行完/回答后清除。</summary>
    internal bool Triggered;

    /// <summary>自动连续吃中(弹窗选"吃!"/对话中触发/弹窗超时)。吃到血回阈值上或没食物。</summary>
    internal bool AutoEating;

    /// <summary>提问弹窗打开时刻(秒),用于超时自动吃。</summary>
    private double PromptOpenedAt;

    /// <summary>弹窗无回应多久后自动吃(秒)。</summary>
    private const double PromptAutoEatSeconds = 3.0;

    private double LastPromptTime = -999.0;

    /// <summary>濒死保护 buff id(Net 同步,联机两端一致)。</summary>
    internal const string InvincibleBuffId = "Claude.AutoEatLowHealth.Invincible";

    /// <summary>保护 buff 时长(毫秒)。保护期每帧刷新,实际由 EndProtection 显式结束。</summary>
    private const int BuffMs = 60000;

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
            Protecting = false;
            PickOtherOpen = false;
            Triggered = false;
            LastPromptTime = -999.0;
            Game1.player.buffs.Remove(InvincibleBuffId);
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

    /// <summary>冷却是否已过(弹窗防轰炸闸)。</summary>
    internal bool CooldownReady()
    {
        if (!Context.IsWorldReady)
            return false;
        double now = Game1.currentGameTime?.TotalGameTime.TotalSeconds ?? 0.0;
        return now - LastPromptTime >= Config.CooldownSeconds;
    }

    internal void MarkPrompted()
    {
        LastPromptTime = Game1.currentGameTime?.TotalGameTime.TotalSeconds ?? 0.0;
    }

    /// <summary>开濒死保护 buff(Net 同步,联机两端一致;CanBeDamaged 前缀挡全部伤害来源)。</summary>
    internal void ApplyProtection()
    {
        if (!Config.InvincibleWhilePrompt)
            return;
        Game1.player.applyBuff(new Buff(
            InvincibleBuffId,
            duration: BuffMs,
            iconTexture: Game1.buffsIcons,
            iconSheetIndex: 8,
            displayName: "濒死保护",
            description: "血量危险!保护期间无敌,请尽快进食。"));
    }

    /// <summary>解除保护 buff(进食动画开始后由原版 isEating 无敌自动接管)。</summary>
    internal void ClearInvincible()
    {
        if (!Config.InvincibleWhilePrompt)
            return;
        Game1.player.buffs.Remove(InvincibleBuffId);
    }

    /// <summary>保护无条件开(保命优先);弹窗受冷却约束(防轰炸)。
    /// 必死一击时即使冷却没过也保命,冷却一过由心跳补弹窗。
    /// 已有弹窗/自选菜单在等玩家时不重复触发,也不重置菜单状态。</summary>
    internal void ProtectAndPrompt(Farmer who)
    {
        if (PromptOpen || PickOtherOpen || AutoEating)
            return;
        Triggered = false;
        ApplyProtection();
        Protecting = true;
        if (CooldownReady())
        {
            MarkPrompted();
            Triggered = true;
            DelayedAction.functionAfterDelay(() =>
            {
                if (who != null && who.IsLocalPlayer && who.health > 0 && Context.IsWorldReady)
                    OnHitLowHealth(who);
            }, 50);
        }
    }

    /// <summary>已保护但还没弹窗(必死拦截时冷却没过 / 当时菜单挡住)→ 冷却过后补弹窗/自动吃。</summary>
    internal void TryPrompt(Farmer who)
    {
        if (PromptOpen || Triggered || AutoEating || !CooldownReady())
            return;
        MarkPrompted();
        Triggered = true;
        OnHitLowHealth(who);
    }

    /// <summary>触发弹窗:无菜单弹窗,对话中自动吃,独占菜单保持保护(等关掉后由心跳补弹窗)。</summary>
    internal void OnHitLowHealth(Farmer who)
    {
        if (who == null || !who.IsLocalPlayer || !Context.IsWorldReady)
            return;

        var menu = Game1.activeClickableMenu;
        if (menu is DialogueBox)
        {
            // 对话阶段:弹窗会被排队/不显示 → 直接自动连吃(按优先级,吃到血回阈值上或没食物)。
            EatByPriority(auto: true);
            PromptOpen = false;
            Triggered = false;
        }
        else if (menu == null)
        {
            PromptOpen = true;
            OpenPrompt();
        }
        else
        {
            // 独占菜单(背包/商店等):不开弹窗,保持保护;菜单一关心跳立刻补弹窗
            Triggered = false;
        }
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        if (!Context.IsWorldReady)
            return;

        Farmer player = Game1.player;

        // 保护期(弹窗 / 自选菜单 / 自动连吃中)全程维持 buff —— 防意外过期,也防菜单切换空窗
        if (Protecting && Config.InvincibleWhilePrompt)
        {
            player.buffs.Remove(InvincibleBuffId);
            ApplyProtection();
        }

        // 提问弹窗超时(无回应)→ 模拟玩家选"吃!"(防 AFK 干等)。
        // answerDialogue 会同步触发 OnAnswer("yes") → 连吃 + 清状态,一条链路,不会双重吃。
        if (PromptOpen)
        {
            double now = Game1.currentGameTime?.TotalGameTime.TotalSeconds ?? 0.0;
            if (now - PromptOpenedAt >= PromptAutoEatSeconds)
            {
                bool answered =
                    Game1.activeClickableMenu is DialogueBox db && db.responses != null &&
                    db.responses.Any(r => r.responseKey == "yes") &&
                    Game1.currentLocation?.answerDialogue(db.responses[0]) == true;
                if (!answered)
                {
                    // 异常兜底(弹窗不是我们的/已被关):强关 + 手动连吃
                    PromptOpen = false;
                    Triggered = false;
                    if (Game1.activeClickableMenu is DialogueBox)
                        Game1.activeClickableMenu = null;
                    EatByPriority(auto: true);
                }
            }
        }

        // 保护期状态机:弹窗被 ESC 关掉 / 自选菜单被关掉 / 连吃完成 → 结束保护
        if (Protecting)
        {
            var menu = Game1.activeClickableMenu;
            if (PickOtherOpen)
            {
                if (menu is not ItemGrabMenu)
                    EndProtection();
            }
            else if (PromptOpen && menu is not DialogueBox)
            {
                EndProtection();
            }
            else if (AutoEating && !player.isEating)
            {
                // 上一口吃完(动画结束 doneEating → isEating=false,回血已结算)→ 血够/没食物结束,否则吃下一口
                // (注意:itemToEat 原版从不置 null,不能用它判断;isEating 才是"正在吃"信号)
                if (player.health >= player.maxHealth * Config.HealthThreshold || !HasAnyFood())
                {
                    EndProtection();
                }
                else
                {
                    EatByPriority(auto: true);
                }
            }
        }

        // 血已回到阈值之上 → 结束保护(玩家自己吃药/回血了)
        if (Protecting && player.health >= player.maxHealth * Config.HealthThreshold)
        {
            EndProtection();
        }

        // 心跳:低血 → 保护;已保护 → 冷却过后补弹窗
        if (player.health <= 0 || player.maxHealth <= 0)
            return;
        if (player.hasBuff(InvincibleBuffId))
        {
            TryPrompt(player);
            return;
        }
        if (player.health < player.maxHealth * Config.HealthThreshold && !Game1.killScreen)
        {
            if (Game1.activeClickableMenu is ReadyCheckDialog)
                return;   // 睡觉等待界面,不动
            ProtectAndPrompt(player);
        }
    }

    private bool HasAnyFood()
    {
        return Game1.player.Items.Any(IsEdible);
    }

    /// <summary>结束本轮保护的唯一出口:清状态 + 清 buff。</summary>
    internal void EndProtection()
    {
        PromptOpen = false;
        PickOtherOpen = false;
        AutoEating = false;
        Protecting = false;
        Triggered = false;
        ClearInvincible();
    }

    private void OpenPrompt()
    {
        PromptOpenedAt = Game1.currentGameTime?.TotalGameTime.TotalSeconds ?? 0.0;
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
        switch (answer)
        {
            case "yes":
                PromptOpen = false;
                Triggered = false;
                // 连续自动吃:保持保护,每口吃完动画结束接着吃,直到血回阈值上或没食物
                EatByPriority(auto: true);
                break;
            case "other":
                PromptOpen = false;
                Triggered = false;
                OpenPickOther();   // 保持保护,菜单开着
                break;
            default:
                EndProtection();
                break;
        }
    }

    private void EatByPriority(bool auto = false)
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
        if (auto)
            AutoEating = food != null;
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
    /// 吃食物。overrideFullness:true —— 原版 Farmer.cs:9138 hasBuff("6") && !overrideFullness
    /// 时拒绝进食(饱食 buff 在,HUD 提示"吃不下了",但调用方已扣食物) → "吃完还在/回不上血"。
    /// 时序:保护 buff 在 eatObject 前保持(eatObject 同步置 isEating=true,原版无敌接管),
    /// 吃完后由 UpdateTicked 状态机收口 —— 连吃模式自动吃下一口,单吃/血回阈值上/没食物才结束保护。
    /// </summary>
    internal void DoEat(Object food)
    {
        Object? eatItem = food.getOne() as Object ?? food;
        if (food.Stack > 1)
            food.Stack--;
        else
            Game1.player.Items.Remove(food);

        // ⚠️ 关键:overrideFullness=true —— 濒死时无视饱食 buff 强制进食
        Game1.player.eatObject(eatItem, overrideFullness: true);
        Game1.addHUDMessage(new HUDMessage("吃掉了 " + food.DisplayName, HUDMessage.achievement_type));
    }

    /// <summary>
    /// 从自选菜单吃(修复"杨桃永不消失"):菜单点击会把物品从背包移到 heldItem(槽位变空),
    /// 回调拿到的是 heldItem 里的原对象 —— 若只吃副本不消费,原对象会被放回背包 → 无限吃。
    /// 照抄原版星之果实分支(heldItem=null 消费):吃一个副本,剩余放回背包,清掉 heldItem。
    /// </summary>
    private void EatFromMenu(Object chosen)
    {
        Object? eatItem = chosen.getOne() as Object;
        if (eatItem == null)
            return;

        if (chosen.Stack > 1)
        {
            chosen.Stack--;
            if (Game1.activeClickableMenu is ItemGrabMenu m)
                m.heldItem = null;
            Game1.player.addItemToInventoryBool(chosen);   // 剩余放回背包
        }
        else
        {
            // 最后一个:吃掉,不再放回 → 从背包消失
            if (Game1.activeClickableMenu is ItemGrabMenu m)
                m.heldItem = null;
        }

        Game1.player.eatObject(eatItem, overrideFullness: true);
        EndProtection();
        Game1.addHUDMessage(new HUDMessage("吃掉了 " + chosen.DisplayName, HUDMessage.achievement_type));
    }

    private void OpenPickOther()
    {
        PickOtherOpen = true;
        Game1.activeClickableMenu = new ItemGrabMenu(
            Game1.player.Items,
            reverseGrab: false,
            showReceivingMenu: false,
            IsEdible,
            (chosen, _) =>
            {
                if (chosen is Object obj && IsEdible(obj))
                    EatFromMenu(obj);
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
        return item is Object obj && obj.Edibility >= 0;
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
            tooltip: () => "触发后/进食期间无敌(通过游戏内 buff 同步,联机房主访客一致;进食动画由原版机制接管)。",
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

/// <summary>濒死保护 buff 生效期间不可受伤 —— 照抄原版雅巴戒指模式(Farmer.cs:7284 !hasBuff("21"))。
/// 所有伤害来源(checkDamage 近战 / 弹射物 / 炸弹 / 火车)最终都走 takeDamage 内的 CanBeDamaged 判断。</summary>
[HarmonyPatch(typeof(Farmer), nameof(Farmer.CanBeDamaged))]
internal static class CanBeDamagedPatch
{
    private static bool Prefix(Farmer __instance, ref bool __result)
    {
        try
        {
            if (__instance.IsLocalPlayer && __instance.hasBuff(ModEntry.InvincibleBuffId))
            {
                __result = false;
                return false;
            }
            return true;
        }
        catch (Exception)
        {
            return true;   // 异常放行原版,绝不拖垮玩家
        }
    }
}

/// <summary>受击前缀:只拦【必死】一击(裸伤害 ≥ 当前血 —— 减伤只会让伤害更低,判断成立)
/// → 开保护 + 拦截。低血触发改由 Postfix 在真实扣血后判定。
/// 拦截不受 InvincibleWhilePrompt 开关影响(保命是核心功能);开关只控制保护 buff。</summary>
[HarmonyPatch(typeof(Farmer), "takeDamage")]
internal static class TakeDamagePatch
{
    private static bool Prefix(Farmer __instance, int damage)
    {
        try
        {
            ModEntry mod = ModEntry.Instance;
            if (mod == null || mod.AutoEating)
                return true;
            if (!__instance.IsLocalPlayer || __instance.temporarilyInvincible || mod.Triggered)
                return true;
            if (__instance.health <= 0)
                return true;

            // 原版实际伤害 = Math.Max(1, damage - Defense[±30% 随机])(Farmer.cs:7343)
            // 减伤只会让伤害更低 → 裸伤害 ≥ 当前血 即必死(减伤后的伤害只会 ≤ damage)
            if (damage >= __instance.health)
            {
                mod.ProtectAndPrompt(__instance);
                return false;   // 拦截必死一击
            }
            return true;
        }
        catch (Exception)
        {
            return true;   // 异常放行原版,绝不拖垮玩家
        }
    }

    /// <summary>受击后缀:真实扣血后判定 —— 低于阈值 → 进入保护流程。
    /// (前缀只拦必死;这里覆盖"没死但已低血",以及真实伤害经减伤后跌破阈值的场景)</summary>
    private static void Postfix(Farmer __instance)
    {
        try
        {
            ModEntry mod = ModEntry.Instance;
            if (mod == null || mod.PromptOpen || mod.Triggered || mod.AutoEating)
                return;
            if (!__instance.IsLocalPlayer || __instance.health <= 0 || __instance.maxHealth <= 0)
                return;
            if (Game1.activeClickableMenu is ReadyCheckDialog)
                return;
            if (__instance.health < __instance.maxHealth * mod.Config.HealthThreshold)
                mod.ProtectAndPrompt(__instance);
        }
        catch (Exception)
        {
            // 异常放行原版
        }
    }
}
