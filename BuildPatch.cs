using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;
using static Mono.Cecil.Cil.OpCodes;

public static class NgInternalPatch
{
    public const string OriginalHash = "78048CDFBF5F5A4321425A047F47C267883FCEBC4ADEF791A941E3292FE1F334";
    private const string Prefix = "NGENUITY.LocalRepair.";
    private static readonly string[] Launchers = {
        "AudioEngineLauncherService", "CommunicationHubLauncherService", "LightEngineLauncherService"
    };

    public static void Build(string original, string common, string donor, string destination)
    {
        if (Hash(original) != OriginalHash)
            throw new InvalidDataException("Unsupported NGENUITY.dll: expected the original 3.2.0 build.");
        const string commonHash = "D4EAE861D1D1F2596CD9B1C41401BE653F60B72094284AA9E724D8E75708C453";
        if (Hash(common) != commonHash)
            throw new InvalidDataException("NGENUITY.Common.dll does not match the supported build.");
        if (Path.GetFullPath(original).Equals(Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Build a separate candidate. Never write the input assembly.");

        using var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(original));
        resolver.AddSearchDirectory(Path.GetDirectoryName(common));
        resolver.AddSearchDirectory(Path.GetDirectoryName(typeof(object).Assembly.Location));
        using var product = ModuleDefinition.ReadModule(original, new ReaderParameters { AssemblyResolver = resolver });
        using var interfaces = ModuleDefinition.ReadModule(common);
        using var helpers = ModuleDefinition.ReadModule(donor);
        ImportHelpers(helpers, product);

        var stopping = Method(product.GetType(Prefix + "SessionExit"), "IsStopping");
        var beginExit = Method(product.GetType(Prefix + "SessionExit"), "BeginExit");
        var declined = Method(product.GetType(Prefix + "SessionExit"), "DeclinedStart");
        var app = product.GetType("HXN.App");
        var request = Method(app, "OnDesktopShutdownRequested");
        // Avalonia can raise this event before deciding whether the app will exit.
        // Keep the existing cleanup in OnDesktopExit and enter it only once.
        request.Body = new MethodBody(request);
        request.Body.Instructions.Add(I(Ret));
        foreach (var attribute in request.CustomAttributes.Where(a =>
                     a.AttributeType.Name == "AsyncStateMachineAttribute").ToArray())
            request.CustomAttributes.Remove(attribute);
        var exit = Method(app, "OnDesktopExit");
        var entry = exit.Body.Instructions[0];
        Before(exit, entry, I(Call, beginExit), I(Brtrue, entry), I(Ret));
        var logger = (MethodReference)AsyncBody(exit).Body.Instructions.First(i =>
            IsCall(i, "Serilog.Log", "Information")).Operand;
        var initialize = Method(app, "Initialize");
        Before(initialize, initialize.Body.Instructions.Single(i => i.OpCode == Ret),
            I(Ldstr, "[LocalAudioPatch] NGENUITY 3.2.0 local repair loaded."), I(Call, logger));
        PatchWindowClosing(product);

        foreach (string name in Launchers)
            PatchLauncher(product.GetType("HXN.Services.Launchers." + name), stopping, declined);
        PatchDisconnect(product);
        PatchBinding(product, interfaces, stopping, logger);
        product.Write(destination, new WriterParameters { DeterministicMvid = true });
    }

    private static void PatchWindowClosing(ModuleDefinition product)
    {
        var window = product.GetType("HXN.Views.MainWindow");
        var closing = Method(window, "OnClosing");
        var keepInTray = At(closing, 0x0010);
        var appState = closing.Body.Variables[0];
        var args = closing.Parameters[0].ParameterType.Resolve();
        var reasonGetter = Method(args, "get_CloseReason");
        var closeReasons = reasonGetter.ReturnType.Resolve();
        int os = Convert.ToInt32(closeReasons.Fields.Single(f => f.Name == "OSShutdown").Constant);
        int app = Convert.ToInt32(closeReasons.Fields.Single(f => f.Name == "ApplicationShutdown").Constant);
        var getReason = product.ImportReference(reasonGetter);
        var baseClosing = product.ImportReference(Method(window.BaseType.Resolve(), "OnClosing"));
        var getUpdating = (MethodReference)At(closing, 0x001b).Operand;
        var getCount = (MethodReference)At(closing, 0x0020).Operand;
        Require(getUpdating.Name == "get_UpdatingDevices" && getCount.Name == "get_Count" &&
                closing.Body.ExceptionHandlers.Count == 1, "Unexpected tray closing logic.");
        var enter = I(Ldarg_1);
        var checkFirmware = I(Ldloc, appState);
        var allowExit = I(Ldarg_0);
        foreach (var i in closing.Body.Instructions)
            if (ReferenceEquals(i.Operand, keepInTray)) i.Operand = enter;
        closing.Body.ExceptionHandlers[0].HandlerEnd = enter;
        // Retain the vendor's close-to-tray behavior for an ordinary close,
        // and its veto during a firmware update.
        Before(closing, keepInTray, enter, I(Callvirt, getReason), I(Ldc_I4, os), I(Beq, checkFirmware),
            I(Ldarg_1), I(Callvirt, getReason), I(Ldc_I4, app), I(Bne_Un, keepInTray), checkFirmware,
            I(Brfalse, allowExit), I(Ldloc, appState), I(Callvirt, getUpdating), I(Callvirt, getCount),
            I(Brtrue, keepInTray), allowExit, I(Ldarg_1), I(Call, baseClosing), I(Ret));
    }

    private static void PatchLauncher(TypeDefinition launcher, MethodDefinition stopping, MethodDefinition declined)
    {
        var onExit = Method(launcher, "OnLauncherExited");
        var entry = onExit.Body.Instructions[0];
        Before(onExit, entry, I(Call, stopping), I(Brfalse, entry), I(Ret));

        var start = Method(launcher, "StartAsync");
        entry = start.Body.Instructions[0];
        Before(start, entry, I(Call, stopping), I(Brfalse, entry), I(Call, declined), I(Ret));

        var exitState = AsyncBody(onExit);
        // Both entries follow acquisition of the launcher's semaphore.
        // The original branch target releases it on the ordinary non-restart path.
        var release = At(exitState, 0x018a);
        Require(release.OpCode == Ldloc_1 &&
                IsCall(release.Next.Next, "System.Threading.SemaphoreSlim", "Release"),
                "Unexpected launcher release path.");
        Before(exitState, At(exitState, 0x0080), I(Call, stopping), I(Brtrue, release));
        Before(exitState, At(exitState, 0x0164), I(Call, stopping), I(Brtrue, release));

        var startState = AsyncBody(start);
        var spawn = startState.Body.Instructions.Single(i => IsCall(i, "System.Diagnostics.Process", "Start"));
        Require(((MethodReference)spawn.Operand).Parameters.Count == 1, "Unexpected process-start signature.");
        var awaitResult = startState.Body.Instructions.Single(i =>
            i.Operand is MethodReference m && m.Name == "SetResult" && m.DeclaringType.Name == "AsyncTaskMethodBuilder`1");
        var result = startState.Body.Variables[2];
        Require(result.VariableType.FullName == "System.Boolean" && awaitResult.Previous.OpCode == Ldloc_2,
                "Unexpected launcher result local.");
        var complete = awaitResult.Previous.Previous.Previous.Previous.Previous.Previous;
        Require(complete.OpCode == Ldarg_0 && startState.Body.Instructions.Any(i =>
            (i.OpCode == Leave || i.OpCode == Leave_S) && ReferenceEquals(i.Operand, complete)),
            "Unexpected launcher completion path.");

        // ProcessStartInfo is already on the evaluation stack at the spawn site.
        Before(startState, spawn, I(Call, stopping), I(Brfalse, spawn), I(Pop),
               I(Ldc_I4_0), I(Stloc, result), I(Leave, complete));
        var publish = startState.Body.Instructions.Single(i =>
            IsCall(i, "CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger", "get_Default"));
        Before(startState, publish, I(Call, stopping), I(Brfalse, publish),
               I(Ldc_I4_0), I(Stloc, result), I(Leave, complete));
    }

    private static void PatchDisconnect(ModuleDefinition product)
    {
        var client = product.GetType("HXN.IPC.IpcClient");
        var disconnect = Method(client, "Disconnect");
        var socketCall = disconnect.Body.Instructions.Single(i => IsCall(i, "NetMQ.NetMQSocket", "Disconnect"));
        var socketMethod = (MethodReference)socketCall.Operand;
        var tolerant = NewMethod(client, "LocalDisconnectEndpoint", product.TypeSystem.Void, isStatic: true);
        tolerant.Parameters.Add(new ParameterDefinition("socket", ParameterAttributes.None, socketMethod.DeclaringType));
        tolerant.Parameters.Add(new ParameterDefinition("address", ParameterAttributes.None, product.TypeSystem.String));
        var start = I(Ldarg_0);
        var alreadyClosed = I(Pop);
        var done = I(Ret);
        Append(tolerant, start, I(Ldarg_1), I(Callvirt, socketMethod), I(Leave, done), alreadyClosed, I(Leave, done), done);
        tolerant.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch) {
            CatchType = new TypeReference("NetMQ", "EndpointNotFoundException", product, socketMethod.DeclaringType.Scope),
            TryStart = start, TryEnd = alreadyClosed, HandlerStart = alreadyClosed, HandlerEnd = done
        });
        socketCall.OpCode = Call;
        socketCall.Operand = tolerant;
    }

    private static void PatchBinding(ModuleDefinition product, ModuleDefinition interfaces,
        MethodDefinition stopping, MethodReference logInfo)
    {
        var manager = product.GetType("HXN.IPC.Managers.AudioEngineManager");
        var main = Method(manager, "InternalSetOutputDeviceName");
        var chat = Method(manager, "InternalSetChatOutputDeviceName");
        var helper = product.GetType(Prefix + "BindingRetry");
        var constructor = Method(manager, ".ctor");
        var register = At(constructor, 0x002a);
        Require(IsCall(register, "CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger", "get_Default"),
                "Unexpected audio manager constructor.");

        var mainField = new FieldDefinition("localMainBinding", FieldAttributes.Private | FieldAttributes.InitOnly, helper);
        var chatField = new FieldDefinition("localChatBinding", FieldAttributes.Private | FieldAttributes.InitOnly, helper);
        manager.Fields.Add(mainField);
        manager.Fields.Add(chatField);

        var canSend = NewMethod(manager, "LocalCanSendBinding", product.TypeSystem.Boolean);
        var no = I(Ldc_I4_0);
        Append(canSend, I(Call, stopping), I(Brtrue, no), I(Ldarg_0),
            I(Call, Method(manager, "get_IsConnected")), I(Brfalse, no), I(Ldarg_0),
            I(Call, Method(manager, "get_ServiceStatus")), I(Ldc_I4_1), I(Ceq), I(Ret), no, I(Ret));

        var response = Method(manager, "DispatchResponse");
        foreach (var binding in new[] {
            (Field: mainField, Original: main, Action: "SetOutputDeviceName"),
            (Field: chatField, Original: chat, Action: "SetChatOutputDeviceName")
        })
        {
            var helperCtor = Method(helper, ".ctor");
            var func = new MethodReference(".ctor", product.TypeSystem.Void, helperCtor.Parameters[1].ParameterType) { HasThis = true };
            var action = new MethodReference(".ctor", product.TypeSystem.Void, helperCtor.Parameters[2].ParameterType) { HasThis = true };
            foreach (var reference in new[] { func, action })
            {
                reference.Parameters.Add(new ParameterDefinition(product.TypeSystem.Object));
                reference.Parameters.Add(new ParameterDefinition(product.TypeSystem.IntPtr));
            }
            Before(constructor, register,
                I(Ldarg_0), I(Ldarg_0), I(Ldfld, manager.Fields.Single(f => f.Name == "_clientLock")),
                I(Ldarg_0), I(Ldftn, canSend), I(Newobj, func),
                I(Ldarg_0), I(Ldftn, binding.Original), I(Newobj, action),
                I(Ldnull), I(Ldftn, logInfo), I(Newobj, action),
                I(Ldstr, binding.Action), I(Newobj, helperCtor), I(Stfld, binding.Field));

            var wrapper = NewMethod(manager, "LocalTrack" + binding.Action, product.TypeSystem.Void);
            wrapper.Parameters.Add(new ParameterDefinition("target", ParameterAttributes.None, product.TypeSystem.String));
            Append(wrapper, I(Ldarg_0), I(Ldfld, binding.Field), I(Ldarg_1), I(Callvirt, Method(helper, "Track")),
                I(Ldarg_0), I(Ldarg_1), I(Call, binding.Original), I(Ret));
            int callers = 0;
            foreach (var type in product.GetTypes())
            foreach (var method in type.Methods.Where(m => m.HasBody && m != wrapper))
            foreach (var i in method.Body.Instructions.Where(i => i.OpCode == Call && ReferenceEquals(i.Operand, binding.Original)))
            {
                i.Operand = wrapper;
                callers++;
            }
            Require(callers == 2, "Unexpected output-binding callers for " + binding.Action);
        }

        var clear = NewMethod(manager, "LocalClearBindingRetries", product.TypeSystem.Void);
        Append(clear, I(Ldarg_0), I(Ldfld, mainField), I(Callvirt, Method(helper, "Clear")),
               I(Ldarg_0), I(Ldfld, chatField), I(Callvirt, Method(helper, "Clear")), I(Ret));
        foreach (string operation in new[] { "StopAsync", "ClearDeviceBaseIds", "InternalSetOutputDeviceFromOSDefault",
                                              "InternalSetOutputDeviceNameAndWaitForDeviceAvailableAsync" })
        {
            var original = Method(manager, operation);
            Before(original, original.Body.Instructions[0], I(Ldarg_0), I(Call, clear));
        }
        var micOnly = Method(manager, "InternalSetMicOnlyMode");
        var micEntry = micOnly.Body.Instructions[0];
        Before(micOnly, micEntry, I(Ldarg_1), I(Brfalse, micEntry), I(Ldarg_0), I(Call, clear));

        var onReply = NewMethod(manager, "LocalHandleBindingReply", product.TypeSystem.Void);
        onReply.Parameters.Add(new ParameterDefinition("action", ParameterAttributes.None, product.TypeSystem.Int32));
        onReply.Parameters.Add(new ParameterDefinition("okay", ParameterAttributes.None, product.TypeSystem.Boolean));
        onReply.Parameters.Add(new ParameterDefinition("message", ParameterAttributes.None, product.TypeSystem.String));
        var enumType = interfaces.GetType("AudioEngine.Action");
        int mainAction = Convert.ToInt32(enumType.Fields.Single(f => f.Name == "SetOutputDeviceName").Constant);
        int chatAction = Convert.ToInt32(enumType.Fields.Single(f => f.Name == "SetChatOutputDeviceName").Constant);
        var tryChat = I(Ldarg_1);
        var finish = I(Ret);
        var reply = Method(helper, "Reply");
        Append(onReply, I(Ldarg_1), I(Ldc_I4, mainAction), I(Bne_Un, tryChat),
            I(Ldarg_0), I(Ldfld, mainField), I(Ldarg_2), I(Ldarg_3), I(Callvirt, reply), I(Ret),
            tryChat, I(Ldc_I4, chatAction), I(Bne_Un, finish),
            I(Ldarg_0), I(Ldfld, chatField), I(Ldarg_2), I(Ldarg_3), I(Callvirt, reply), finish);

        var actionGet = (MethodReference)At(response, 0x002c).Operand;
        var errorGet = (MethodReference)At(response, 0x0054).Operand;
        var ackParam = response.Parameters[0];
        var error = response.Body.Variables[1];
        Require(actionGet.Name == "get_Action" && errorGet.DeclaringType.FullName == "AudioEngine.Error" &&
                At(response, 0x003d).OpCode == Ret && At(response, 0x005e).OpCode == Ret,
                "Unexpected binding acknowledgement path.");
        Before(response, At(response, 0x003d), I(Ldarg_0), I(Ldarga, ackParam),
            I(Call, actionGet), I(Ldc_I4_1), I(Ldnull), I(Call, onReply));
        Before(response, At(response, 0x005e), I(Ldarg_0), I(Ldarga, ackParam),
            I(Call, actionGet), I(Ldc_I4_0), I(Ldloca, error), I(Call, errorGet), I(Call, onReply));
    }

    private static void ImportHelpers(ModuleDefinition donor, ModuleDefinition target)
    {
        var types = new Dictionary<TypeDefinition, TypeDefinition>();
        var fields = new Dictionary<FieldDefinition, FieldDefinition>();
        var methods = new Dictionary<MethodDefinition, MethodDefinition>();
        foreach (var source in donor.Types.Where(t => t.Namespace == Prefix.TrimEnd('.')))
        {
            var copy = new TypeDefinition(source.Namespace, source.Name, source.Attributes, target.TypeSystem.Object);
            target.Types.Add(copy);
            types.Add(source, copy);
        }
        Require(types.Count == 2, "Unexpected helper assembly contents.");
        TypeReference MapType(TypeReference t) =>
            t is TypeDefinition d && types.TryGetValue(d, out var mapped) ? mapped : target.ImportReference(t);
        foreach (var pair in types)
        {
            foreach (var f in pair.Key.Fields)
            {
                var field = new FieldDefinition(f.Name, f.Attributes, MapType(f.FieldType));
                if (f.HasConstant) field.Constant = f.Constant;
                pair.Value.Fields.Add(field);
                fields.Add(f, field);
            }
            foreach (var m in pair.Key.Methods)
            {
                var method = new MethodDefinition(m.Name, m.Attributes, MapType(m.ReturnType)) {
                    ImplAttributes = m.ImplAttributes
                };
                foreach (var p in m.Parameters)
                    method.Parameters.Add(new ParameterDefinition(p.Name, p.Attributes, MapType(p.ParameterType)));
                if (m.HasPInvokeInfo)
                {
                    var info = m.PInvokeInfo;
                    var dll = target.ModuleReferences.FirstOrDefault(r => r.Name == info.Module.Name);
                    if (dll == null)
                    {
                        dll = new ModuleReference(info.Module.Name);
                        target.ModuleReferences.Add(dll);
                    }
                    method.PInvokeInfo = new PInvokeInfo(info.Attributes, info.EntryPoint, dll);
                }
                pair.Value.Methods.Add(method);
                methods.Add(m, method);
            }
        }
        foreach (var pair in methods.Where(p => p.Key.HasBody))
        {
            var from = pair.Key.Body;
            var to = pair.Value.Body;
            to.InitLocals = from.InitLocals;
            foreach (var v in from.Variables)
                to.Variables.Add(new VariableDefinition(MapType(v.VariableType)));
            var instructions = new Dictionary<Instruction, Instruction>();
            foreach (var source in from.Instructions)
            {
                var copy = I(Nop);
                copy.OpCode = source.OpCode;
                to.Instructions.Add(copy);
                instructions.Add(source, copy);
            }
            Instruction MapInstruction(Instruction i) => i == null ? null : instructions[i];
            foreach (var instruction in instructions)
            {
                instruction.Value.Operand = instruction.Key.Operand switch {
                    null => null,
                    Instruction i => MapInstruction(i),
                    Instruction[] a => a.Select(MapInstruction).ToArray(),
                    VariableDefinition v => to.Variables[v.Index],
                    ParameterDefinition p => p.Index < 0 ? to.ThisParameter : pair.Value.Parameters[p.Index],
                    MethodDefinition m when methods.TryGetValue(m, out var mappedMethod) => mappedMethod,
                    MethodReference m => target.ImportReference(m),
                    FieldDefinition f when fields.TryGetValue(f, out var mappedField) => mappedField,
                    FieldReference f => target.ImportReference(f),
                    TypeReference t => MapType(t),
                    string or int or long or sbyte or byte or float or double => instruction.Key.Operand,
                    _ => throw new InvalidDataException("Unsupported helper IL operand.")
                };
            }
            foreach (var h in from.ExceptionHandlers)
                to.ExceptionHandlers.Add(new ExceptionHandler(h.HandlerType) {
                    TryStart = MapInstruction(h.TryStart), TryEnd = MapInstruction(h.TryEnd),
                    HandlerStart = MapInstruction(h.HandlerStart), HandlerEnd = MapInstruction(h.HandlerEnd),
                    FilterStart = MapInstruction(h.FilterStart), CatchType = h.CatchType == null ? null : MapType(h.CatchType)
                });
        }
    }

    private static MethodDefinition AsyncBody(MethodDefinition method)
    {
        var attribute = method.CustomAttributes.Single(a => a.AttributeType.Name == "AsyncStateMachineAttribute");
        var stateMachine = ((TypeReference)attribute.ConstructorArguments[0].Value).Resolve();
        return Method(stateMachine, "MoveNext");
    }

    private static MethodDefinition NewMethod(TypeDefinition owner, string name, TypeReference returnType, bool isStatic = false)
    {
        var attributes = MethodAttributes.Private | MethodAttributes.HideBySig;
        if (isStatic) attributes |= MethodAttributes.Static;
        var method = new MethodDefinition(name, attributes, returnType);
        owner.Methods.Add(method);
        return method;
    }

    private static MethodDefinition Method(TypeDefinition type, string name) => type.Methods.Single(m => m.Name == name);
    private static Instruction At(MethodDefinition method, int offset) => method.Body.Instructions.Single(i => i.Offset == offset);
    private static bool IsCall(Instruction i, string type, string name) =>
        (i.OpCode == Call || i.OpCode == Callvirt) && i.Operand is MethodReference m && m.DeclaringType.FullName == type && m.Name == name;
    private static void Before(MethodDefinition m, Instruction anchor, params Instruction[] block)
    {
        m.Body.SimplifyMacros();
        foreach (var i in block) m.Body.GetILProcessor().InsertBefore(anchor, i);
    }
    private static void Append(MethodDefinition m, params Instruction[] block)
    {
        foreach (var i in block) m.Body.Instructions.Add(i);
    }
    private static Instruction I(OpCode code) => Instruction.Create(code);
    private static Instruction I(OpCode code, int value) => Instruction.Create(code, value);
    private static Instruction I(OpCode code, string value) => Instruction.Create(code, value);
    private static Instruction I(OpCode code, Instruction value) => Instruction.Create(code, value);
    private static Instruction I(OpCode code, FieldReference value) => Instruction.Create(code, value);
    private static Instruction I(OpCode code, MethodReference value) => Instruction.Create(code, value);
    private static Instruction I(OpCode code, VariableDefinition value) => Instruction.Create(code, value);
    private static Instruction I(OpCode code, ParameterDefinition value) => Instruction.Create(code, value);
    private static void Require(bool valid, string reason)
    {
        if (!valid) throw new InvalidDataException(reason);
    }
    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
