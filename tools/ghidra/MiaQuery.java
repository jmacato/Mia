// Concise headless queries for the Mia AVR firmware.
// @category Mia

import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileResults;
import ghidra.app.cmd.disassemble.DisassembleCommand;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.address.AddressSet;
import ghidra.program.model.address.AddressSpace;
import ghidra.program.model.listing.Function;
import ghidra.program.model.listing.Instruction;
import ghidra.program.model.listing.InstructionIterator;
import ghidra.program.model.lang.Register;
import ghidra.program.model.lang.RegisterValue;
import ghidra.program.model.mem.MemoryBlock;
import ghidra.program.model.symbol.Reference;
import ghidra.program.model.symbol.ReferenceIterator;

import java.math.BigInteger;
import java.util.regex.Pattern;

public class MiaQuery extends GhidraScript {
    private DecompInterface decompiler;

    @Override
    protected void run() throws Exception {
        String[] args = getScriptArgs();
        if (args.length == 0) {
            throw new IllegalArgumentException("query mode is required");
        }

        String mode = args[0];
        decompiler = new DecompInterface();
        decompiler.openProgram(currentProgram);
        try {
            switch (mode) {
                case "function":
                    requireAddresses(args);
                    dumpFunctions(args, false, true, true);
                    break;
                case "containing":
                    requireAddresses(args);
                    dumpFunctions(args, true, true, true);
                    break;
                case "decompile":
                    requireAddresses(args);
                    dumpFunctions(args, false, false, true);
                    break;
                case "decompile-containing":
                    requireAddresses(args);
                    dumpFunctions(args, true, false, true);
                    break;
                case "listing":
                    requireAddresses(args);
                    dumpFunctions(args, false, true, false);
                    break;
                case "listing-containing":
                    requireAddresses(args);
                    dumpFunctions(args, true, true, false);
                    break;
                case "refs":
                    requireAddresses(args);
                    dumpReferences(args);
                    break;
                case "callers":
                    requireAddresses(args);
                    dumpCallers(args);
                    break;
                case "call-sites":
                    requireAddresses(args);
                    dumpCallSites(args);
                    break;
                case "callees":
                    requireAddresses(args);
                    dumpCallees(args);
                    break;
                case "range":
                    dumpRange(args);
                    break;
                case "linear-range":
                    dumpLinearRange(args);
                    break;
                case "search-instructions":
                    searchInstructions(args);
                    break;
                case "info":
                    dumpInfo();
                    break;
                case "repair-function":
                    repairFunction(args);
                    break;
                case "repair-thumb-function":
                    repairThumbFunction(args);
                    break;
                default:
                    throw new IllegalArgumentException("unknown query mode: " + mode);
            }
        } finally {
            decompiler.dispose();
        }
    }

    private void requireAddresses(String[] args) {
        if (args.length < 2) {
            throw new IllegalArgumentException("at least one address is required");
        }
    }

    private Address parseMiaAddress(String text) {
        String spaceName = currentProgram.getAddressFactory().getDefaultAddressSpace().getName();
        String offsetText = text;
        int colon = text.indexOf(':');
        if (colon >= 0) {
            spaceName = text.substring(0, colon);
            offsetText = text.substring(colon + 1);
        }
        offsetText = offsetText.replaceFirst("^(0x|0X)", "");
        AddressSpace space = currentProgram.getAddressFactory().getAddressSpace(spaceName);
        if (space == null) {
            throw new IllegalArgumentException("unknown address space: " + spaceName);
        }
        long offset = Long.parseUnsignedLong(offsetText, 16);
        if (spaceName.equals("mem") && offset > 0xffff) {
            long ramp = offset >>> 16;
            long logical = offset & 0xffff;
            println("note: physical data 0x" + Long.toHexString(offset) + " maps to mem:" +
                Long.toHexString(logical) + " with RAMP=0x" + Long.toHexString(ramp));
            offset = logical;
        }
        Address address = currentProgram.getAddressFactory()
            .getAddress(spaceName + ":" + Long.toHexString(offset));
        if (address == null) {
            throw new IllegalArgumentException("invalid address: " + text);
        }
        return address;
    }

    private Function resolveFunction(Address address, boolean containing, boolean create) throws Exception {
        Function function = containing ? getFunctionContaining(address) : getFunctionAt(address);
        if (function == null && !containing && create) {
            disassemble(address);
            function = createFunction(address, "firmware_" + Long.toHexString(address.getOffset()));
        }
        return function;
    }

    private void dumpFunctions(String[] args, boolean containing, boolean listing, boolean decompile)
            throws Exception {
        for (int i = 1; i < args.length; i++) {
            Address query = parseMiaAddress(args[i]);
            Function function = resolveFunction(query, containing, true);
            println("\n===== query " + query + " function " + describe(function) + " =====");
            if (function == null) {
                continue;
            }
            if (listing) {
                dumpListing(function);
            }
            if (decompile) {
                dumpDecompile(function);
            }
        }
    }

    private void dumpListing(Function function) {
        for (Instruction instruction : currentProgram.getListing().getInstructions(function.getBody(), true)) {
            println(formatInstruction(instruction));
        }
    }

    private void dumpDecompile(Function function) {
        DecompileResults result = decompiler.decompileFunction(function, 60, monitor);
        if (result.decompileCompleted() && result.getDecompiledFunction() != null) {
            println("\n----- decompile " + function.getEntryPoint() + " -----");
            println(result.getDecompiledFunction().getC());
        } else {
            printerr("Decompiler failed for " + function.getEntryPoint() + ": " + result.getErrorMessage());
        }
    }

    private void dumpReferences(String[] args) {
        for (int i = 1; i < args.length; i++) {
            Address target = parseMiaAddress(args[i]);
            println("\n===== references to " + target + " =====");
            ReferenceIterator references = currentProgram.getReferenceManager().getReferencesTo(target);
            int count = 0;
            while (references.hasNext()) {
                Reference reference = references.next();
                Function source = getFunctionContaining(reference.getFromAddress());
                println(reference.getFromAddress() + "  " + reference.getReferenceType() +
                    "  from=" + describe(source));
                count++;
            }
            println("count=" + count);
        }
    }

    private void dumpCallers(String[] args) throws Exception {
        for (int i = 1; i < args.length; i++) {
            Address query = parseMiaAddress(args[i]);
            Function function = resolveFunction(query, false, true);
            println("\n===== callers of " + describe(function) + " =====");
            if (function == null) {
                continue;
            }
            ReferenceIterator references = currentProgram.getReferenceManager()
                .getReferencesTo(function.getEntryPoint());
            int count = 0;
            while (references.hasNext()) {
                Reference reference = references.next();
                if (!reference.getReferenceType().isCall()) {
                    continue;
                }
                Function caller = getFunctionContaining(reference.getFromAddress());
                println(reference.getFromAddress() + "  " + describe(caller));
                count++;
            }
            println("count=" + count);
        }
    }

    private void dumpCallSites(String[] args) throws Exception {
        for (int i = 1; i < args.length; i++) {
            Address query = parseMiaAddress(args[i]);
            Function function = resolveFunction(query, false, true);
            println("\n===== call sites of " + describe(function) + " =====");
            if (function == null) {
                continue;
            }

            ReferenceIterator references = currentProgram.getReferenceManager()
                .getReferencesTo(function.getEntryPoint());
            int count = 0;
            while (references.hasNext()) {
                Reference reference = references.next();
                if (!reference.getReferenceType().isCall()) {
                    continue;
                }

                Instruction call = currentProgram.getListing()
                    .getInstructionAt(reference.getFromAddress());
                Function caller = getFunctionContaining(reference.getFromAddress());
                println("\n--- " + reference.getFromAddress() + "  " + describe(caller) + " ---");
                if (call == null) {
                    println("<call instruction not defined>");
                    continue;
                }

                Instruction cursor = call;
                for (int before = 0; before < 2; before++) {
                    Instruction previous = currentProgram.getListing()
                        .getInstructionBefore(cursor.getAddress());
                    if (previous == null || caller == null ||
                            !caller.getBody().contains(previous.getAddress())) {
                        break;
                    }
                    cursor = previous;
                }
                for (int context = 0; cursor != null && context < 8; context++) {
                    if (caller != null && !caller.getBody().contains(cursor.getAddress())) {
                        break;
                    }
                    println(formatInstruction(cursor));
                    cursor = currentProgram.getListing().getInstructionAfter(cursor.getAddress());
                }
                count++;
            }
            println("\ncount=" + count);
        }
    }

    private void dumpCallees(String[] args) throws Exception {
        for (int i = 1; i < args.length; i++) {
            Address query = parseMiaAddress(args[i]);
            Function function = resolveFunction(query, false, true);
            println("\n===== callees of " + describe(function) + " =====");
            if (function == null) {
                continue;
            }
            int count = 0;
            InstructionIterator instructions = currentProgram.getListing()
                .getInstructions(function.getBody(), true);
            while (instructions.hasNext()) {
                Instruction instruction = instructions.next();
                for (Reference reference : instruction.getReferencesFrom()) {
                    if (!reference.getReferenceType().isCall()) {
                        continue;
                    }
                    Function callee = getFunctionAt(reference.getToAddress());
                    println(instruction.getAddress() + " -> " + reference.getToAddress() +
                        "  " + describe(callee));
                    count++;
                }
            }
            println("count=" + count);
        }
    }

    private void dumpRange(String[] args) throws Exception {
        if (args.length != 3) {
            throw new IllegalArgumentException("range requires START and END");
        }
        Address start = parseMiaAddress(args[1]);
        Address end = parseMiaAddress(args[2]);
        if (!start.getAddressSpace().equals(end.getAddressSpace()) || start.compareTo(end) >= 0) {
            throw new IllegalArgumentException("range must be a non-empty interval in one address space");
        }
        disassemble(start);
        AddressSet body = new AddressSet(start, end.previous());
        println("\n===== range " + start + ".." + end + " =====");
        for (Instruction instruction : currentProgram.getListing().getInstructions(body, true)) {
            println(formatInstruction(instruction));
        }
    }

    private void dumpLinearRange(String[] args) throws Exception {
        if (args.length != 3) {
            throw new IllegalArgumentException("linear-range requires START and END");
        }
        Address start = parseMiaAddress(args[1]);
        Address end = parseMiaAddress(args[2]);
        if (!start.getAddressSpace().equals(end.getAddressSpace()) || start.compareTo(end) >= 0) {
            throw new IllegalArgumentException(
                "linear-range must be a non-empty interval in one address space");
        }

        AddressSet restricted = new AddressSet(start, end.previous());
        Address cursor = start;
        int seeds = 0;
        while (cursor != null && cursor.compareTo(end) < 0) {
            Instruction instruction = currentProgram.getListing().getInstructionContaining(cursor);
            if (instruction != null) {
                cursor = instruction.getMaxAddress().next();
                continue;
            }

            DisassembleCommand command = new DisassembleCommand(cursor, restricted, true);
            command.enableCodeAnalysis(false);
            command.applyTo(currentProgram, monitor);
            seeds++;
            instruction = currentProgram.getListing().getInstructionAt(cursor);
            cursor = instruction == null ? cursor.next() : instruction.getMaxAddress().next();
        }

        println("\n===== linear range " + start + ".." + end + " seeds=" + seeds + " =====");
        for (Instruction instruction :
                currentProgram.getListing().getInstructions(restricted, true)) {
            println(formatInstruction(instruction));
        }
    }

    private void searchInstructions(String[] args) {
        if (args.length != 2 && args.length != 4) {
            throw new IllegalArgumentException(
                "search-instructions requires REGEX and optional START END");
        }

        Pattern pattern = Pattern.compile(args[1], Pattern.CASE_INSENSITIVE);
        int count = 0;
        InstructionIterator instructions;
        if (args.length == 4) {
            Address start = parseMiaAddress(args[2]);
            Address end = parseMiaAddress(args[3]);
            if (!start.getAddressSpace().equals(end.getAddressSpace()) ||
                    start.compareTo(end) >= 0) {
                throw new IllegalArgumentException(
                    "search interval must be non-empty and in one address space");
            }
            instructions = currentProgram.getListing().getInstructions(
                new AddressSet(start, end.previous()),
                true);
        } else {
            instructions = currentProgram.getListing().getInstructions(true);
        }
        while (instructions.hasNext()) {
            Instruction instruction = instructions.next();
            if (!pattern.matcher(instruction.toString()).find()) {
                continue;
            }
            Function function = getFunctionContaining(instruction.getAddress());
            println(formatInstruction(instruction) + "  ; function=" + describe(function));
            count++;
        }
        println("count=" + count);
    }

    private String formatInstruction(Instruction instruction) {
        StringBuilder line = new StringBuilder();
        line.append(instruction.getAddress()).append("  ").append(instruction);
        Address[] flows = instruction.getFlows();
        if (flows.length > 0) {
            line.append("  ; flow=");
            for (int i = 0; i < flows.length; i++) {
                if (i > 0) {
                    line.append(',');
                }
                line.append(flows[i]);
            }
        }
        return line.toString();
    }

    private void dumpInfo() {
        println("name=" + currentProgram.getName());
        println("language=" + currentProgram.getLanguageID());
        println("compiler=" + currentProgram.getCompilerSpec().getCompilerSpecID());
        println("executable=" + currentProgram.getExecutablePath());
        println("md5=" + currentProgram.getExecutableMD5());
        println("image-base=" + currentProgram.getImageBase());
        for (MemoryBlock block : currentProgram.getMemory().getBlocks()) {
            println("block=" + block.getName() + " " + block.getStart() + ".." + block.getEnd() +
                " size=0x" + Long.toHexString(block.getSize()));
        }
    }

    private void repairFunction(String[] args) throws Exception {
        if (args.length != 3) {
            throw new IllegalArgumentException("repair-function requires START and END");
        }
        Address start = parseMiaAddress(args[1]);
        Address end = parseMiaAddress(args[2]);
        if (!start.getAddressSpace().equals(end.getAddressSpace()) || start.compareTo(end) >= 0) {
            throw new IllegalArgumentException(
                "repair-function must be a non-empty interval in one address space");
        }
        clearListing(start, end.previous());
        disassemble(start);
        Function function = createFunction(start, "firmware_" + Long.toHexString(start.getOffset()));
        println("\n===== repaired " + start + ".." + end + " function " + describe(function) + " =====");
        if (function != null) {
            dumpListing(function);
            dumpDecompile(function);
        }
    }

    private void repairThumbFunction(String[] args) throws Exception {
        if (args.length != 3) {
            throw new IllegalArgumentException("repair-thumb-function requires START and END");
        }
        Address start = parseMiaAddress(args[1]);
        Address end = parseMiaAddress(args[2]);
        if (!start.getAddressSpace().equals(end.getAddressSpace()) || start.compareTo(end) >= 0) {
            throw new IllegalArgumentException(
                "repair-thumb-function must be a non-empty interval in one address space");
        }

        Register tmode = currentProgram.getProgramContext().getRegister("TMode");
        if (tmode == null) {
            throw new IllegalArgumentException("program language does not expose ARM TMode");
        }

        Address last = end.previous();
        clearListing(start, last);
        currentProgram.getProgramContext().setValue(tmode, start, last, BigInteger.ONE);
        AddressSet restricted = new AddressSet(start, last);
        DisassembleCommand command = new DisassembleCommand(start, restricted, true);
        command.setInitialContext(new RegisterValue(tmode, BigInteger.ONE));
        command.enableCodeAnalysis(false);
        command.applyTo(currentProgram, monitor);

        Function function = createFunction(start, "firmware_" + Long.toHexString(start.getOffset()));
        println("\n===== repaired Thumb " + start + ".." + end + " function " +
            describe(function) + " =====");
        if (function != null) {
            dumpListing(function);
            dumpDecompile(function);
        }
    }

    private String describe(Function function) {
        if (function == null) {
            return "<none>";
        }
        return function.getName() + "@" + function.getEntryPoint();
    }
}
