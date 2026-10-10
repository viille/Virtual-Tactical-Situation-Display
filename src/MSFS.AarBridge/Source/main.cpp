#include <MSFS/MSFS.h>
#include <MSFS/MSFS_CommBus.h>
#include <MSFS/MSFS_Vars.h>
#include <SimParamArrayHelper.h>

#include <rapidjson/document.h>
#include <rapidjson/stringbuffer.h>
#include <rapidjson/writer.h>

#include <algorithm>
#include <cmath>
#include <cstdint>
#include <ctime>
#include <cstdio>
#include <cstring>
#include <utility>
#include <string>
#include <vector>

namespace {

constexpr char kRequestEvent[] = "VTSD_AAR_REQUEST";
constexpr char kResponseEvent[] = "VTSD_AAR_RESPONSE";
constexpr char kBridgeVersion[] = "1.1.9";
constexpr int kProtocolVersion = 1;
constexpr int kMaximumTankIndex = 64;
constexpr double kPoundsToKg = 0.45359237;
constexpr double kEpsilon = 0.0001;

struct Tank {
    int index;
    std::string id;
    FsAVarId quantityVar;
    FsAVarId capacityVar;
    double quantityGallons;
    double capacityGallons;
    bool writable;
    FsAVarId writeQuantityVar = 0;
    bool writeQuantityWithoutParams = false;
    std::string writeQuantityName;
};

struct FuelState {
    double kgPerGallon;
    double totalKg;
    double capacityKg;
    bool legacy;
    std::vector<Tank> tanks;
    bool writable;
};

struct LegacyTankDefinition {
    const char* id;
    const char* quantityName;
    const char* capacityName;
    FsAVarId quantityVar;
    FsAVarId capacityVar;
};

FsAVarId g_newFuelSystem = 0;
FsAVarId g_tankQuantity = 0;
FsAVarId g_tankCapacity = 0;
FsAVarId g_fuelWeightPerGallon = 0;
FsAVarId g_legacyTotalQuantity = 0;
FsAVarId g_legacyTotalCapacity = 0;
FsUnitId g_boolUnit = 0;
FsUnitId g_numberUnit = 0;
FsUnitId g_gallonsUnit = 0;
FsUnitId g_poundsPerGallonUnit = 0;
bool g_a32nxRefuelPending = false;
bool g_a32nxSavedRateValid = false;
double g_a32nxSavedRate = 0;
FsLVarId g_a32nxRefuelStarted = FS_VAR_INVALID_ID;
FsLVarId g_a32nxRefuelRate = FS_VAR_INVALID_ID;
bool g_registered = false;
std::vector<std::pair<std::string, double>> g_writeProbeSignature;
std::string g_fuelReadOnlyReason = "fuel capability has not been probed";
std::string g_fuelReadDiagnostic = "fuel state has not been checked";
std::vector<std::string> g_writeProbeDiagnostics;
LegacyTankDefinition g_legacyTankDefinitions[] = {
    { "center", "FUEL TANK CENTER QUANTITY", "FUEL TANK CENTER CAPACITY", 0, 0 },
    { "center2", "FUEL TANK CENTER2 QUANTITY", "FUEL TANK CENTER2 CAPACITY", 0, 0 },
    { "center3", "FUEL TANK CENTER3 QUANTITY", "FUEL TANK CENTER3 CAPACITY", 0, 0 },
    { "external1", "FUEL TANK EXTERNAL1 QUANTITY", "FUEL TANK EXTERNAL1 CAPACITY", 0, 0 },
    { "external2", "FUEL TANK EXTERNAL2 QUANTITY", "FUEL TANK EXTERNAL2 CAPACITY", 0, 0 },
    { "left-aux", "FUEL TANK LEFT AUX QUANTITY", "FUEL TANK LEFT AUX CAPACITY", 0, 0 },
    { "left-main", "FUEL TANK LEFT MAIN QUANTITY", "FUEL TANK LEFT MAIN CAPACITY", 0, 0 },
    { "left-tip", "FUEL TANK LEFT TIP QUANTITY", "FUEL TANK LEFT TIP CAPACITY", 0, 0 },
    { "right-aux", "FUEL TANK RIGHT AUX QUANTITY", "FUEL TANK RIGHT AUX CAPACITY", 0, 0 },
    { "right-main", "FUEL TANK RIGHT MAIN QUANTITY", "FUEL TANK RIGHT MAIN CAPACITY", 0, 0 },
    { "right-tip", "FUEL TANK RIGHT TIP QUANTITY", "FUEL TANK RIGHT TIP CAPACITY", 0, 0 }
};

struct ParamArray {
    FsVarParamArray value;
    explicit ParamArray(int index) : value(FsCreateParamArray("i", static_cast<unsigned int>(index))) {}
    ~ParamArray() { FsDestroyParamArray(&value); }
    ParamArray(const ParamArray&) = delete;
    ParamArray& operator=(const ParamArray&) = delete;
};

std::string TimestampUtc() {
    const std::time_t now = std::time(nullptr);
    std::tm* utc = std::gmtime(&now);
    if (utc == nullptr) return "";
    char buffer[32]{};
    if (std::strftime(buffer, sizeof(buffer), "%Y-%m-%dT%H:%M:%SZ", utc) == 0) return "";
    return buffer;
}

bool ReadVar(FsAVarId id, FsUnitId unit, const FsVarParamArray& params, double& value,
    const char* variableName, std::string& diagnostic) {
    if (id == 0) {
        diagnostic = std::string("AVar ID is unavailable: ") + variableName;
        return false;
    }
    if (unit == 0) {
        diagnostic = std::string("unit ID is unavailable for ") + variableName;
        return false;
    }
    const auto error = fsVarsAVarGet(id, unit, params, &value, FS_OBJECT_ID_USER_AIRCRAFT);
    if (error != FS_VAR_ERROR_NONE) {
        char message[192]{};
        std::snprintf(message, sizeof(message), "AVar read failed: name=%s error=%u id=%d unit=%d",
            variableName, static_cast<unsigned int>(error), id, unit);
        diagnostic = message;
        return false;
    }
    if (!std::isfinite(value)) {
        diagnostic = std::string("AVar returned a non-finite value: ") + variableName;
        return false;
    }
    return true;
}

bool ReadTankQuantity(const Tank& tank, bool legacy, double& value, std::string& diagnostic) {
    if (legacy) {
        const FsVarParamArray noParams{};
        return ReadVar(tank.quantityVar, g_gallonsUnit, noParams, value, tank.id.c_str(), diagnostic);
    }
    ParamArray params(tank.index);
    return ReadVar(tank.quantityVar, g_gallonsUnit, params.value, value, "FUELSYSTEM TANK QUANTITY", diagnostic);
}

bool WriteTankQuantity(const Tank& tank, bool legacy, double value, double& readBack, std::string& diagnostic,
    const char* method = "auto") {
    FsVarError setterResult = FS_VAR_ERROR_NONE;
    const bool useAlias = legacy || (std::strcmp(method, "aliases") == 0 && tank.writeQuantityVar != 0) ||
        (std::strcmp(method, "auto") == 0 && tank.writeQuantityWithoutParams);
    if (useAlias) {
        const FsVarParamArray noParams{};
        const auto variable = tank.writeQuantityVar != 0 && !legacy ? tank.writeQuantityVar : tank.quantityVar;
        setterResult = fsVarsAVarSet(variable, g_gallonsUnit, noParams, value, FS_OBJECT_ID_USER_AIRCRAFT);
    } else {
        ParamArray params(tank.index);
        const auto variable = tank.writeQuantityVar != 0 ? tank.writeQuantityVar : tank.quantityVar;
        setterResult = fsVarsAVarSet(variable, g_gallonsUnit, params.value, value, FS_OBJECT_ID_USER_AIRCRAFT);
    }
    if (setterResult != FS_VAR_ERROR_NONE) {
        char message[192]{};
        std::snprintf(message, sizeof(message), "AVar write failed: tank=%s variable=%s error=%u", tank.id.c_str(),
            tank.writeQuantityName.empty() ? (legacy ? tank.id.c_str() : "FUELSYSTEM TANK QUANTITY") : tank.writeQuantityName.c_str(),
            static_cast<unsigned int>(setterResult));
        diagnostic = message;
        return false;
    }
    const bool read = ReadTankQuantity(tank, legacy, readBack, diagnostic);
    if (!read || std::abs(readBack - value) > 0.01) {
        if (diagnostic.empty()) {
            char message[192]{};
            std::snprintf(message, sizeof(message), "AVar read-back differed: tank=%s variable=%s requestedGal=%.4f readBackGal=%.4f",
                tank.id.c_str(), tank.writeQuantityName.empty() ? "FUELSYSTEM TANK QUANTITY" : tank.writeQuantityName.c_str(), value, readBack);
            diagnostic = message;
        }
        return false;
    }
    return true;
}

bool AttachA330FuelAliases(std::vector<Tank>& tanks) {
    static constexpr const char* kAliasIds[] = { "center", "left-main", "right-main", "left-aux", "right-aux" };
    if (tanks.size() != std::size(kAliasIds)) return false;

    for (size_t i = 0; i < tanks.size(); ++i) {
        auto& tank = tanks[i];
        const LegacyTankDefinition* definition = nullptr;
        for (const auto& candidate : g_legacyTankDefinitions) {
            if (candidate.id == std::string(kAliasIds[i])) { definition = &candidate; break; }
        }
        if (definition == nullptr || definition->quantityVar == 0 || definition->capacityVar == 0) return false;

        const FsVarParamArray noParams{};
        double aliasQuantity = 0;
        double aliasCapacity = 0;
        std::string diagnostic;
        if (!ReadVar(definition->quantityVar, g_gallonsUnit, noParams, aliasQuantity, definition->quantityName, diagnostic) ||
            !ReadVar(definition->capacityVar, g_gallonsUnit, noParams, aliasCapacity, definition->capacityName, diagnostic) ||
            std::abs(aliasCapacity - tank.capacityGallons) > 0.1 ||
            std::abs(aliasQuantity - tank.quantityGallons) > 0.1) return false;

        tank.writeQuantityVar = definition->quantityVar;
        tank.writeQuantityWithoutParams = true;
        tank.writeQuantityName = definition->quantityName;
    }
    return true;
}

FsLVarId FindLVar(const char* name) {
    const char* normalized = std::strncmp(name, "L:", 2) == 0 ? name + 2 : name;
    const auto id = fsVarsGetLVarId(normalized);
    return id == FS_VAR_INVALID_ID ? FS_VAR_INVALID_ID : id;
}

bool HasA32nxRefuelController() {
    static constexpr const char* kTargetVars[] = {
        "A32NX_FUEL_CENTER_DESIRED",
        "A32NX_FUEL_LEFT_MAIN_DESIRED",
        "A32NX_FUEL_RIGHT_MAIN_DESIRED",
        "A32NX_FUEL_LEFT_AUX_DESIRED",
        "A32NX_FUEL_RIGHT_AUX_DESIRED"
    };
    if (FindLVar("A32NX_REFUEL_STARTED_BY_USR") == FS_VAR_INVALID_ID ||
        FindLVar("A32NX_EFB_REFUEL_RATE_SETTING") == FS_VAR_INVALID_ID) return false;
    for (const auto* name : kTargetVars) if (FindLVar(name) == FS_VAR_INVALID_ID) return false;
    return true;
}

bool FinishA32nxRefuel() {
    if (!g_a32nxRefuelPending) return true;
    bool success = true;
    if (g_a32nxRefuelStarted != FS_VAR_INVALID_ID && g_boolUnit != 0)
        success = fsVarsLVarSet(g_a32nxRefuelStarted, g_boolUnit, 0) == FS_VAR_ERROR_NONE && success;
    if (g_a32nxSavedRateValid && g_a32nxRefuelRate != FS_VAR_INVALID_ID && g_numberUnit != 0)
        success = fsVarsLVarSet(g_a32nxRefuelRate, g_numberUnit, g_a32nxSavedRate) == FS_VAR_ERROR_NONE && success;
    g_a32nxRefuelPending = false;
    g_a32nxSavedRateValid = false;
    return success;
}

bool StartA32nxRefuel(const FuelState& before, const std::vector<double>& targetGallons, std::string& diagnostic) {
    static constexpr const char* kTargetVars[] = {
        "A32NX_FUEL_CENTER_DESIRED",
        "A32NX_FUEL_LEFT_MAIN_DESIRED",
        "A32NX_FUEL_RIGHT_MAIN_DESIRED",
        "A32NX_FUEL_LEFT_AUX_DESIRED",
        "A32NX_FUEL_RIGHT_AUX_DESIRED"
    };
    if (before.legacy || before.tanks.size() != std::size(kTargetVars)) {
        diagnostic = "A32NX refuel controller requires the recognized five-tank modern fuel layout";
        return false;
    }
    for (const auto* name : kTargetVars) {
        if (FindLVar(name) == FS_VAR_INVALID_ID) {
            diagnostic = std::string("A32NX refuel controller variable is unavailable: ") + name;
            return false;
        }
    }

    g_a32nxRefuelStarted = FindLVar("A32NX_REFUEL_STARTED_BY_USR");
    g_a32nxRefuelRate = FindLVar("A32NX_EFB_REFUEL_RATE_SETTING");
    if (g_a32nxRefuelStarted == FS_VAR_INVALID_ID || g_a32nxRefuelRate == FS_VAR_INVALID_ID || g_boolUnit == 0 || g_numberUnit == 0) {
        diagnostic = "A32NX refuel controller variables or units are unavailable";
        return false;
    }
    double currentRate = 0;
    if (fsVarsLVarGet(g_a32nxRefuelRate, g_numberUnit, &currentRate) != FS_VAR_ERROR_NONE || !std::isfinite(currentRate)) {
        diagnostic = "could not read the current A32NX refuel rate";
        return false;
    }

    FsLVarId targetIds[std::size(kTargetVars)]{};
    for (size_t i = 0; i < std::size(kTargetVars); ++i) targetIds[i] = FindLVar(kTargetVars[i]);
    for (size_t i = 0; i < std::size(kTargetVars); ++i) {
        if (fsVarsLVarSet(targetIds[i], g_numberUnit, targetGallons[i]) != FS_VAR_ERROR_NONE) {
            diagnostic = std::string("could not set A32NX fuel target: ") + kTargetVars[i];
            return false;
        }
    }

    g_a32nxSavedRate = currentRate;
    g_a32nxSavedRateValid = true;
    if (fsVarsLVarSet(g_a32nxRefuelRate, g_numberUnit, 2) != FS_VAR_ERROR_NONE) {
        diagnostic = "could not select A32NX instant refuel mode";
        g_a32nxSavedRateValid = false;
        return false;
    }
    if (fsVarsLVarSet(g_a32nxRefuelStarted, g_boolUnit, 1) != FS_VAR_ERROR_NONE) {
        fsVarsLVarSet(g_a32nxRefuelRate, g_numberUnit, currentRate);
        diagnostic = "could not start the A32NX refuel controller";
        g_a32nxSavedRateValid = false;
        return false;
    }
    g_a32nxRefuelPending = true;
    diagnostic = "A32NX refuel controller accepted the tank targets";
    return true;
}

bool ReadFuelState(FuelState& result, bool probeWrites) {
    g_fuelReadDiagnostic.clear();
    if (probeWrites) { g_writeProbeSignature.clear(); g_writeProbeDiagnostics.clear(); g_fuelReadOnlyReason.clear(); }
    const FsVarParamArray noParams{};
    double modernFuelSystem = 0;
    std::string readDiagnostic;
    if (!ReadVar(g_newFuelSystem, g_boolUnit, noParams, modernFuelSystem, "NEW FUEL SYSTEM", readDiagnostic)) {
        g_fuelReadDiagnostic = readDiagnostic;
        g_writeProbeSignature.clear();
        if (probeWrites) g_fuelReadOnlyReason = g_fuelReadDiagnostic;
        return false;
    }
    double poundsPerGallon = 0;
    if (!ReadVar(g_fuelWeightPerGallon, g_poundsPerGallonUnit, noParams, poundsPerGallon, "FUEL WEIGHT PER GALLON", readDiagnostic)) {
        g_fuelReadDiagnostic = readDiagnostic;
        g_writeProbeSignature.clear();
        if (probeWrites) g_fuelReadOnlyReason = g_fuelReadDiagnostic;
        return false;
    }
    if (poundsPerGallon <= 0) {
        g_fuelReadDiagnostic = "FUEL WEIGHT PER GALLON returned a non-positive value";
        g_writeProbeSignature.clear();
        if (probeWrites) g_fuelReadOnlyReason = g_fuelReadDiagnostic;
        return false;
    }

    result = {};
    result.legacy = modernFuelSystem < 0.5;
    result.kgPerGallon = poundsPerGallon * kPoundsToKg;

    if (result.legacy) {
        double totalQuantityGallons = 0;
        double totalCapacityGallons = 0;
        if (!ReadVar(g_legacyTotalQuantity, g_gallonsUnit, noParams, totalQuantityGallons, "FUEL TOTAL QUANTITY", readDiagnostic) ||
            !ReadVar(g_legacyTotalCapacity, g_gallonsUnit, noParams, totalCapacityGallons, "FUEL TOTAL CAPACITY", readDiagnostic)) {
            g_fuelReadDiagnostic = readDiagnostic;
            g_writeProbeSignature.clear();
            if (probeWrites) g_fuelReadOnlyReason = g_fuelReadDiagnostic;
            return false;
        }
        if (totalCapacityGallons <= kEpsilon || totalQuantityGallons < -kEpsilon || totalQuantityGallons > totalCapacityGallons + 0.05) {
            g_fuelReadDiagnostic = "legacy aggregate fuel quantity or capacity is invalid";
            g_writeProbeSignature.clear();
            if (probeWrites) g_fuelReadOnlyReason = g_fuelReadDiagnostic;
            return false;
        }

        unsigned int legacyTankCandidates = 0;
        unsigned int legacyTankIdsAvailable = 0;
        unsigned int resolvedLegacyTanks = 0;
        unsigned int readableLegacyTanks = 0;
        unsigned int emptyLegacyTanks = 0;
        std::string firstLegacyTankDiagnostic;
        for (const auto& definition : g_legacyTankDefinitions) {
            ++legacyTankCandidates;
            if (definition.quantityVar == 0 || definition.capacityVar == 0) {
                if (firstLegacyTankDiagnostic.empty()) firstLegacyTankDiagnostic = std::string("AVar ID unavailable: ") +
                    (definition.capacityVar == 0 ? definition.capacityName : definition.quantityName);
                continue;
            }
            ++legacyTankIdsAvailable;
            ++resolvedLegacyTanks;
            double capacityGallons = 0;
            double quantityGallons = 0;
            std::string capacityDiagnostic;
            std::string quantityDiagnostic;
            const FsVarParamArray noTankParams{};
            const bool hasCapacity = ReadVar(definition.capacityVar, g_gallonsUnit, noTankParams, capacityGallons, definition.capacityName, capacityDiagnostic);
            const bool hasQuantity = ReadVar(definition.quantityVar, g_gallonsUnit, noTankParams, quantityGallons, definition.quantityName, quantityDiagnostic);
            if (!hasCapacity || !hasQuantity) {
                if (firstLegacyTankDiagnostic.empty()) firstLegacyTankDiagnostic = !hasCapacity ? capacityDiagnostic : quantityDiagnostic;
                continue;
            }
            ++readableLegacyTanks;
            if (capacityGallons <= kEpsilon) {
                ++emptyLegacyTanks;
                continue;
            }
            if (quantityGallons < -kEpsilon || quantityGallons > capacityGallons + 0.05) {
                char message[192]{};
                std::snprintf(message, sizeof(message), "invalid legacy tank values: tank=%s quantityGallons=%.4f capacityGallons=%.4f",
                    definition.id, quantityGallons, capacityGallons);
                g_fuelReadDiagnostic = message;
                g_writeProbeSignature.clear();
                if (probeWrites) g_fuelReadOnlyReason = g_fuelReadDiagnostic;
                return false;
            }

            Tank tank{ 0, definition.id, definition.quantityVar, definition.capacityVar,
                std::max(0.0, quantityGallons), capacityGallons, false };
            std::string writeDiagnostic;
            double readBack = quantityGallons;
            const bool writable = !probeWrites || WriteTankQuantity(tank, true, quantityGallons, readBack, writeDiagnostic);
            tank.writable = probeWrites && writable;
            if (probeWrites) {
                char diagnostic[224]{};
                std::snprintf(diagnostic, sizeof(diagnostic), "tank=%s beforeGal=%.4f readBackGal=%.4f writable=%s detail=%s",
                    definition.id, quantityGallons, readBack, writable ? "true" : "false",
                    writeDiagnostic.empty() ? "none" : writeDiagnostic.c_str());
                g_writeProbeDiagnostics.emplace_back(diagnostic);
                if (!writable && g_fuelReadOnlyReason.empty()) g_fuelReadOnlyReason = writeDiagnostic.empty()
                    ? "legacy tank same-value write probe read-back differed"
                    : writeDiagnostic;
            }
            result.tanks.push_back(tank);
            result.totalKg += tank.quantityGallons * result.kgPerGallon;
            result.capacityKg += tank.capacityGallons * result.kgPerGallon;
        }

        double discoveredQuantityGallons = 0;
        double discoveredCapacityGallons = 0;
        for (const auto& tank : result.tanks) {
            discoveredQuantityGallons += tank.quantityGallons;
            discoveredCapacityGallons += tank.capacityGallons;
        }
        const double capacityTolerance = std::max(0.25, totalCapacityGallons * 0.0001);
        const double quantityTolerance = std::max(0.25, totalQuantityGallons * 0.0001);
        if (result.tanks.empty() || std::abs(discoveredCapacityGallons - totalCapacityGallons) > capacityTolerance ||
            std::abs(discoveredQuantityGallons - totalQuantityGallons) > quantityTolerance) {
            char message[384]{};
            std::snprintf(message, sizeof(message),
                "legacy tank discovery mismatch: candidates=%u ids=%u tanks=%u resolved=%u readable=%u empty=%u tankQtyGal=%.3f totalQtyGal=%.3f tankCapacityGal=%.3f totalCapacityGal=%.3f firstIssue=%s",
                legacyTankCandidates, legacyTankIdsAvailable, static_cast<unsigned int>(result.tanks.size()),
                resolvedLegacyTanks, readableLegacyTanks, emptyLegacyTanks,
                discoveredQuantityGallons, totalQuantityGallons,
                discoveredCapacityGallons, totalCapacityGallons, firstLegacyTankDiagnostic.empty() ? "none" : firstLegacyTankDiagnostic.c_str());
            g_fuelReadDiagnostic = message;
            g_writeProbeSignature.clear();
            if (probeWrites) g_fuelReadOnlyReason = g_fuelReadDiagnostic;
            return false;
        }

        result.totalKg = totalQuantityGallons * result.kgPerGallon;
        result.capacityKg = totalCapacityGallons * result.kgPerGallon;
        result.writable = !result.tanks.empty() && (!probeWrites || g_writeProbeDiagnostics.size() == result.tanks.size()) &&
            std::all_of(result.tanks.begin(), result.tanks.end(), [](const Tank& tank) { return tank.writable; });
        if (probeWrites) {
            g_writeProbeSignature.clear();
            if (result.writable) for (const auto& tank : result.tanks) g_writeProbeSignature.emplace_back(tank.id, tank.capacityGallons);
        } else if (!g_writeProbeSignature.empty()) {
            bool sameStructure = result.tanks.size() == g_writeProbeSignature.size();
            for (size_t i = 0; sameStructure && i < result.tanks.size(); ++i) {
                sameStructure = result.tanks[i].id == g_writeProbeSignature[i].first &&
                    std::abs(result.tanks[i].capacityGallons - g_writeProbeSignature[i].second) <= 0.01;
            }
            if (sameStructure) {
                for (auto& tank : result.tanks) tank.writable = true;
                result.writable = true;
                g_fuelReadOnlyReason.clear();
            } else {
                std::fprintf(stderr, "[VTSD AAR Bridge] Legacy tank topology changed; re-probing write capability for the current aircraft.\n");
                return ReadFuelState(result, true);
            }
        }
        char readSummary[224]{};
        std::snprintf(readSummary, sizeof(readSummary), "legacy read succeeded: tanks=%u capacityKg=%.2f", static_cast<unsigned int>(result.tanks.size()), result.capacityKg);
        g_fuelReadDiagnostic = readSummary;
        if (probeWrites && !result.writable && g_fuelReadOnlyReason.empty()) g_fuelReadOnlyReason = "one or more legacy tanks failed the write probe";
        return true;
    }

    bool foundTank = false;
    int scanStoppedAtIndex = 0;
    for (int index = 1; index <= kMaximumTankIndex; ++index) {
        ParamArray params(index);
        double capacityGallons = 0;
        double quantityGallons = 0;
        std::string capacityDiagnostic;
        std::string quantityDiagnostic;
        const bool hasCapacity = ReadVar(g_tankCapacity, g_gallonsUnit, params.value, capacityGallons,
            "FUELSYSTEM TANK USABLE CAPACITY", capacityDiagnostic);
        const bool hasQuantity = ReadVar(g_tankQuantity, g_gallonsUnit, params.value, quantityGallons,
            "FUELSYSTEM TANK QUANTITY", quantityDiagnostic);
        if (!hasCapacity || !hasQuantity) {
            if (!foundTank) {
                g_fuelReadDiagnostic = !hasCapacity ? capacityDiagnostic : quantityDiagnostic;
                g_writeProbeSignature.clear();
                if (probeWrites) g_fuelReadOnlyReason = g_fuelReadDiagnostic;
                return false;
            }
            scanStoppedAtIndex = index;
            break;
        }
        if (capacityGallons <= kEpsilon) {
            if (!foundTank) {
                g_fuelReadDiagnostic = "tank index 1 has no positive usable capacity; no tanks discovered";
                g_writeProbeSignature.clear();
                if (probeWrites) g_fuelReadOnlyReason = g_fuelReadDiagnostic;
                return false;
            }
            scanStoppedAtIndex = index;
            break;
        }
        if (quantityGallons < -kEpsilon || quantityGallons > capacityGallons + 0.05) {
            char message[192]{};
            std::snprintf(message, sizeof(message), "invalid tank values: index=%d quantityGallons=%.4f capacityGallons=%.4f",
                index, quantityGallons, capacityGallons);
            g_fuelReadDiagnostic = message;
            g_writeProbeSignature.clear();
            if (probeWrites) g_fuelReadOnlyReason = g_fuelReadDiagnostic;
            return false;
        }

        result.tanks.push_back({ index, std::string("tank-") + std::to_string(index), g_tankQuantity, g_tankCapacity,
            std::max(0.0, quantityGallons), capacityGallons, false });
        result.totalKg += std::max(0.0, quantityGallons) * result.kgPerGallon;
        result.capacityKg += capacityGallons * result.kgPerGallon;
        foundTank = true;
    }

    const bool a330AliasesAttached = AttachA330FuelAliases(result.tanks);
    if (probeWrites) {
        for (auto& tank : result.tanks) {
            double readBack = tank.quantityGallons;
            std::string writeDiagnostic;
            tank.writable = WriteTankQuantity(tank, false, tank.quantityGallons, readBack, writeDiagnostic);
            char diagnostic[256]{};
            std::snprintf(diagnostic, sizeof(diagnostic), "tank=%s variable=%s beforeGal=%.4f readBackGal=%.4f writable=%s detail=%s",
                tank.id.c_str(), tank.writeQuantityName.empty() ? "FUELSYSTEM TANK QUANTITY[index]" : tank.writeQuantityName.c_str(),
                tank.quantityGallons, readBack, tank.writable ? "true" : "false",
                writeDiagnostic.empty() ? "none" : writeDiagnostic.c_str());
            g_writeProbeDiagnostics.emplace_back(diagnostic);
            if (!tank.writable && g_fuelReadOnlyReason.empty())
                g_fuelReadOnlyReason = writeDiagnostic.empty() ? "same-value write probe read-back differed or failed" : writeDiagnostic;
        }
        if (a330AliasesAttached)
            std::fprintf(stderr, "[VTSD AAR Bridge] A330-compatible five-tank layout detected; using aircraft fuel tank quantity aliases for writes.\n");
    }

    result.writable = foundTank && result.tanks.size() < static_cast<size_t>(kMaximumTankIndex) &&
        (!probeWrites || g_writeProbeDiagnostics.size() == result.tanks.size());
    for (const auto& tank : result.tanks) result.writable = result.writable && tank.writable;

    if (probeWrites) {
        g_writeProbeSignature.clear();
        if (result.writable) {
            for (const auto& tank : result.tanks) g_writeProbeSignature.emplace_back(tank.id, tank.capacityGallons);
        }
    } else if (result.tanks.size() == g_writeProbeSignature.size() && !g_writeProbeSignature.empty()) {
        bool sameStructure = true;
        for (size_t i = 0; i < result.tanks.size(); ++i) {
            sameStructure = sameStructure && result.tanks[i].id == g_writeProbeSignature[i].first &&
                std::abs(result.tanks[i].capacityGallons - g_writeProbeSignature[i].second) <= 0.01;
        }
        if (sameStructure) {
            for (auto& tank : result.tanks) tank.writable = true;
            result.writable = true;
            g_fuelReadOnlyReason.clear();
        } else {
            std::fprintf(stderr, "[VTSD AAR Bridge] Tank topology changed; re-probing write capability for the current aircraft.\n");
            return ReadFuelState(result, true);
        }
    } else if (!g_writeProbeSignature.empty()) {
        std::fprintf(stderr, "[VTSD AAR Bridge] Tank count changed; re-probing write capability for the current aircraft.\n");
        return ReadFuelState(result, true);
    }
    if (!foundTank || result.capacityKg <= 0) {
        g_writeProbeSignature.clear();
        g_fuelReadDiagnostic = "no usable fuel tanks discovered";
        if (probeWrites) g_fuelReadOnlyReason = g_fuelReadDiagnostic;
        return false;
    }
    char readSummary[192]{};
    if (scanStoppedAtIndex > 0)
        std::snprintf(readSummary, sizeof(readSummary), "read succeeded: tanks=%u capacityKg=%.2f scanStoppedAtIndex=%d",
            static_cast<unsigned int>(result.tanks.size()), result.capacityKg, scanStoppedAtIndex);
    else
        std::snprintf(readSummary, sizeof(readSummary), "read succeeded: tanks=%u capacityKg=%.2f scanLimitReached=%s",
            static_cast<unsigned int>(result.tanks.size()), result.capacityKg, result.tanks.size() >= static_cast<size_t>(kMaximumTankIndex) ? "true" : "false");
    g_fuelReadDiagnostic = readSummary;
    if (probeWrites && !result.writable && g_fuelReadOnlyReason.empty()) g_fuelReadOnlyReason = "one or more discovered tanks failed the write probe or tank scan limit";
    return true;
}

std::vector<std::string> GetWriteMethods(FuelState& state) {
    std::vector<std::string> methods;
    if (!state.legacy && state.tanks.size() == 5 && AttachA330FuelAliases(state.tanks) &&
        HasA32nxRefuelController()) {
        // The aircraft's own controller must own fuel changes; direct SimVar writes
        // are immediately reverted by the A32NX/Headwind fuel simulation.
        methods.emplace_back("a32nx-native");
        return methods;
    }
    if (state.legacy) {
        methods.emplace_back("legacy");
        return methods;
    }
    if (state.tanks.size() == 5 && AttachA330FuelAliases(state.tanks))
        methods.emplace_back("aliases");
    methods.emplace_back("modern");
    return methods;
}

bool SendResponse(const std::string& payload) {
    return fsCommBusCall(kResponseEvent, payload.c_str(), static_cast<unsigned int>(payload.size() + 1), FsCommBusBroadcast_SimConnect);
}

bool SendResponse(const std::string& requestId, const std::string& action, const char* status,
    const char* error = nullptr, const FuelState* fuel = nullptr, double requestedKg = 0, double appliedKg = 0,
    const std::vector<std::string>* capabilities = nullptr, const std::vector<std::string>* writeMethods = nullptr) {
    rapidjson::Document document(rapidjson::kObjectType);
    auto& allocator = document.GetAllocator();
    document.AddMember("requestId", rapidjson::Value(requestId.c_str(), allocator), allocator);
    document.AddMember("action", rapidjson::Value(action.c_str(), allocator), allocator);
    document.AddMember("protocolVersion", kProtocolVersion, allocator);
    document.AddMember("bridgeVersion", rapidjson::Value(kBridgeVersion, allocator), allocator);
    document.AddMember("status", rapidjson::Value(status, allocator), allocator);
    if (error != nullptr) document.AddMember("error", rapidjson::Value(error, allocator), allocator);
    if (action == "GET_CAPABILITIES" || action == "GET_FUEL_STATE") {
        rapidjson::Value capabilityList(rapidjson::kArrayType);
        if (capabilities != nullptr) {
            for (const auto& capability : *capabilities)
                capabilityList.PushBack(rapidjson::Value(capability.c_str(), allocator), allocator);
        }
        document.AddMember("capabilities", capabilityList, allocator);
        rapidjson::Value diagnostics(rapidjson::kObjectType);
        diagnostics.AddMember("fuelReadDiagnostic", rapidjson::Value(g_fuelReadDiagnostic.c_str(), allocator), allocator);
        diagnostics.AddMember("fuelReadOnlyReason", rapidjson::Value(g_fuelReadOnlyReason.c_str(), allocator), allocator);
        rapidjson::Value probes(rapidjson::kArrayType);
        for (const auto& line : g_writeProbeDiagnostics)
            probes.PushBack(rapidjson::Value(line.c_str(), allocator), allocator);
        diagnostics.AddMember("writeProbe", probes, allocator);
        rapidjson::Value methods(rapidjson::kArrayType);
        if (writeMethods != nullptr) for (const auto& method : *writeMethods)
            methods.PushBack(rapidjson::Value(method.c_str(), allocator), allocator);
        diagnostics.AddMember("writeMethods", methods, allocator);
        document.AddMember("diagnostics", diagnostics, allocator);
    }
    if (fuel != nullptr) {
        rapidjson::Value state(rapidjson::kObjectType);
        const auto sampledAtUtc = TimestampUtc();
        state.AddMember("sampledAtUtc", rapidjson::Value(sampledAtUtc.c_str(), allocator), allocator);
        state.AddMember("currentFuelKg", fuel->totalKg, allocator);
        state.AddMember("capacityKg", fuel->capacityKg, allocator);
        state.AddMember("fuelWeightPerGallonLb", fuel->kgPerGallon / kPoundsToKg, allocator);
        rapidjson::Value tanks(rapidjson::kArrayType);
        for (const auto& tank : fuel->tanks) {
            rapidjson::Value item(rapidjson::kObjectType);
            item.AddMember("tankId", rapidjson::Value(tank.id.c_str(), allocator), allocator);
            item.AddMember("currentKg", tank.quantityGallons * fuel->kgPerGallon, allocator);
            item.AddMember("capacityKg", tank.capacityGallons * fuel->kgPerGallon, allocator);
            item.AddMember("writable", tank.writable, allocator);
            tanks.PushBack(item, allocator);
        }
        state.AddMember("tanks", tanks, allocator);
        document.AddMember("fuelState", state, allocator);
    }
    if (action == "APPLY_FUEL_DELTA") {
        document.AddMember("requestedKg", requestedKg, allocator);
        document.AddMember("appliedKg", appliedKg, allocator);
    }
    rapidjson::StringBuffer buffer;
    rapidjson::Writer<rapidjson::StringBuffer> writer(buffer);
    document.Accept(writer);
    return SendResponse(std::string(buffer.GetString(), buffer.GetSize()));
}

void ApplyFuelDelta(const std::string& requestId, const std::string& action, double requestedKg, const std::string& method) {
    std::fprintf(stderr, "[VTSD AAR Bridge] APPLY_FUEL_DELTA received: requestId=%s method=%s requestedKg=%.2f.\n",
        requestId.c_str(), method.c_str(), requestedKg);
    if (!std::isfinite(requestedKg) || std::abs(requestedKg) < kEpsilon) {
        std::fprintf(stderr, "[VTSD AAR Bridge] APPLY_FUEL_DELTA rejected: delta is not finite or is zero.\n");
        SendResponse(requestId, action, "Failed", "Fuel delta must be finite and non-zero.", nullptr, requestedKg);
        return;
    }

    FuelState before{};
    if (!ReadFuelState(before, true)) {
        const auto* reason = g_fuelReadOnlyReason.empty() ? g_fuelReadDiagnostic.c_str() : g_fuelReadOnlyReason.c_str();
        std::fprintf(stderr, "[VTSD AAR Bridge] APPLY_FUEL_DELTA rejected: current aircraft fuel is not writable; reason=%s.\n",
            reason[0] == '\0' ? "unknown" : reason);
        SendResponse(requestId, action, "Failed", "This aircraft fuel system is not safely writable.", nullptr, requestedKg);
        return;
    }
    std::string selectedMethod = method;
    if (selectedMethod == "auto" && !before.legacy && before.tanks.size() == 5 &&
        AttachA330FuelAliases(before.tanks) && HasA32nxRefuelController()) {
        selectedMethod = "a32nx-native";
        std::fprintf(stderr, "[VTSD AAR Bridge] Auto-selected A32NX refuel controller for the recognized five-tank aircraft.\n");
    }
    if (selectedMethod == "legacy" && !before.legacy) {
        SendResponse(requestId, action, "Failed", "Legacy write method does not match the current aircraft fuel system.", nullptr, requestedKg);
        return;
    }
    if (selectedMethod == "a32nx-native" || selectedMethod == "aliases") {
        if (before.legacy || before.tanks.size() != 5 || !AttachA330FuelAliases(before.tanks)) {
            SendResponse(requestId, action, "Failed", "The selected five-tank A330 write method does not match the current aircraft.", nullptr, requestedKg);
            return;
        }
        if (selectedMethod == "a32nx-native" && !HasA32nxRefuelController()) {
            SendResponse(requestId, action, "Failed", "A32NX refuel controller variables were not found on the current aircraft.", nullptr, requestedKg);
            return;
        }
    }
    if (selectedMethod == "modern" && before.legacy) {
        SendResponse(requestId, action, "Failed", "Modern write method does not match the current aircraft fuel system.", nullptr, requestedKg);
        return;
    }
    if (selectedMethod != "auto" && selectedMethod != "a32nx-native" && selectedMethod != "aliases" && selectedMethod != "modern" && selectedMethod != "legacy") {
        SendResponse(requestId, action, "Failed", "Unknown fuel write method.", nullptr, requestedKg);
        return;
    }

    std::vector<double> weights(before.tanks.size());
    double totalWeight = 0;
    for (size_t i = 0; i < before.tanks.size(); ++i) {
        const auto& tank = before.tanks[i];
        weights[i] = requestedKg > 0
            ? std::max(0.0, tank.capacityGallons - tank.quantityGallons)
            : std::max(0.0, tank.quantityGallons);
        totalWeight += weights[i];
    }
    const double targetGallons = std::min(std::abs(requestedKg) / before.kgPerGallon, totalWeight);
    if (targetGallons <= kEpsilon) {
        SendResponse(requestId, action, "Failed", "Fuel capacity or empty boundary reached.", nullptr, requestedKg);
        return;
    }
    std::vector<double> targetTankGallons(before.tanks.size());
    double remainder = targetGallons;
    size_t finalWeightedIndex = before.tanks.size();
    for (size_t i = 0; i < weights.size(); ++i) if (weights[i] > kEpsilon) finalWeightedIndex = i;
    for (size_t i = 0; i < before.tanks.size(); ++i) {
        if (weights[i] <= kEpsilon) continue;
        const double allocation = i == finalWeightedIndex
            ? remainder
            : std::min(remainder, targetGallons * weights[i] / totalWeight);
        remainder -= allocation;
        const auto& tank = before.tanks[i];
        const double sign = requestedKg > 0 ? 1.0 : -1.0;
        const double nextQuantity = std::max(0.0, std::min(tank.quantityGallons + sign * allocation, tank.capacityGallons));
        targetTankGallons[i] = nextQuantity;
    }

    if (selectedMethod == "a32nx-native") {
        std::string diagnostic;
        if (!StartA32nxRefuel(before, targetTankGallons, diagnostic)) {
            std::fprintf(stderr, "[VTSD AAR Bridge] A32NX native refuel rejected: %s.\n", diagnostic.c_str());
            SendResponse(requestId, action, "Failed", diagnostic.c_str(), nullptr, requestedKg);
            return;
        }
        std::fprintf(stderr, "[VTSD AAR Bridge] A32NX native refuel started; waiting for persistent tank read-back.\n");
        SendResponse(requestId, action, "Pending", nullptr, &before, requestedKg, 0);
        return;
    }

    bool allWritesSucceeded = true;
    for (size_t i = 0; i < before.tanks.size() && allWritesSucceeded; ++i) {
        const auto& tank = before.tanks[i];
        double readBack = 0;
        std::string readBackDiagnostic;
        allWritesSucceeded = WriteTankQuantity(tank, before.legacy, targetTankGallons[i], readBack, readBackDiagnostic,
            selectedMethod == "auto" ? "auto" : selectedMethod.c_str());
    }

    FuelState after{};
    if (!ReadFuelState(after, false)) {
        std::fprintf(stderr, "[VTSD AAR Bridge] APPLY_FUEL_DELTA readback failed: requestedKg=%.2f beforeKg=%.2f tanks=%u reason=%s.\n",
            requestedKg, before.totalKg, static_cast<unsigned int>(before.tanks.size()),
            g_fuelReadDiagnostic.empty() ? "unknown" : g_fuelReadDiagnostic.c_str());
        SendResponse(requestId, action, "Failed", "Fuel mutation could not be verified by simulator read-back.", nullptr, requestedKg);
        return;
    }

    const double signedAppliedKg = after.totalKg - before.totalKg;
    const double appliedKg = std::abs(signedAppliedKg);
    if (!std::isfinite(signedAppliedKg) || std::abs(signedAppliedKg) > std::abs(requestedKg) + 0.05 ||
        (appliedKg > 0.01 && std::signbit(signedAppliedKg) != std::signbit(requestedKg))) {
        std::fprintf(stderr, "[VTSD AAR Bridge] APPLY_FUEL_DELTA readback mismatch: requestedKg=%.2f beforeKg=%.2f afterKg=%.2f appliedKg=%.2f.\n",
            requestedKg, before.totalKg, after.totalKg, signedAppliedKg);
        SendResponse(requestId, action, "Failed", "Simulator read-back did not match the requested direction or bound.", &after, requestedKg);
        return;
    }

    const bool complete = allWritesSucceeded && std::abs(signedAppliedKg - requestedKg) <= 0.05;
    const char* status = complete ? "Success" : appliedKg > 0.01 ? "Partial" : "Failed";
    std::fprintf(stderr, "[VTSD AAR Bridge] APPLY_FUEL_DELTA result: status=%s requestedKg=%.2f appliedKg=%.2f beforeKg=%.2f afterKg=%.2f tanks=%u writesSucceeded=%s.\n",
        status, requestedKg, signedAppliedKg, before.totalKg, after.totalKg,
        static_cast<unsigned int>(after.tanks.size()), allWritesSucceeded ? "true" : "false");
    SendResponse(requestId, action, status,
        complete ? nullptr : "Fuel transfer was partial or a tank write failed; further transfer must stop.", &after, requestedKg, signedAppliedKg);
}

void OnRequest(const char* bytes, unsigned int size, void*) {
    if (bytes == nullptr || size == 0 || size > 8192) return;
    while (size > 0 && bytes[size - 1] == '\0') --size;
    if (size == 0) return;
    rapidjson::Document request;
    request.Parse(bytes, size);
    if (request.HasParseError() || !request.IsObject()) return;
    const auto requestIdValue = request.FindMember("requestId");
    const auto actionValue = request.FindMember("action");
    if (requestIdValue == request.MemberEnd() || !requestIdValue->value.IsString() ||
        actionValue == request.MemberEnd() || !actionValue->value.IsString()) return;
    const std::string requestId(requestIdValue->value.GetString(), requestIdValue->value.GetStringLength());
    const std::string action(actionValue->value.GetString(), actionValue->value.GetStringLength());
    if (requestId.empty() || requestId.size() > 64) return;
    const bool traceHandshake = action == "HELLO";
    if (traceHandshake) std::fprintf(stderr, "[VTSD AAR Bridge] HELLO request received by WASM callback.\n");

    if (action == "APPLY_FUEL_DELTA") {
        const auto deltaValue = request.FindMember("deltaKg");
        const auto protocolValue = request.FindMember("protocolVersion");
        if (deltaValue == request.MemberEnd() || !deltaValue->value.IsNumber() ||
            protocolValue == request.MemberEnd() || !protocolValue->value.IsInt()) {
            SendResponse(requestId, action, "Failed", "APPLY_FUEL_DELTA requires protocolVersion and numeric deltaKg.");
            return;
        }
        const int protocolVersion = protocolValue->value.GetInt();
        if (protocolVersion != kProtocolVersion) {
            SendResponse(requestId, action, "Failed", "Bridge protocol version mismatch.");
            return;
        }

        const double deltaKg = deltaValue->value.GetDouble();
        std::string method = "auto";
        const auto methodValue = request.FindMember("writeMethod");
        if (methodValue != request.MemberEnd() && methodValue->value.IsString())
            method.assign(methodValue->value.GetString(), methodValue->value.GetStringLength());
        ApplyFuelDelta(requestId, action, deltaKg, method);
        return;
    }

    const auto protocolValue = request.FindMember("protocolVersion");
    if (action != "HELLO" && protocolValue != request.MemberEnd() && protocolValue->value.IsInt() &&
        protocolValue->value.GetInt() != kProtocolVersion) {
        SendResponse(requestId, action, "Failed", "Bridge protocol version mismatch.");
        return;
    }

    if (action == "HELLO") {
        const bool sent = SendResponse(requestId, action, "Success");
        std::fprintf(stderr, "[VTSD AAR Bridge] HELLO response CommBus send %s.\n", sent ? "succeeded" : "FAILED");
    } else if (action == "GET_CAPABILITIES") {
        std::vector<std::string> capabilities;
        std::vector<std::string> writeMethods;
        FuelState state{};
        if (ReadFuelState(state, false)) {
            capabilities.emplace_back("fuel.read");
            if (ReadFuelState(state, true)) writeMethods = GetWriteMethods(state);
            if (!writeMethods.empty()) capabilities.emplace_back("fuel.write");
        }
        std::fprintf(stderr, "[VTSD AAR Bridge] GET_CAPABILITIES fuel.read=%s fuel.write=%s; %s\n",
            std::find(capabilities.begin(), capabilities.end(), "fuel.read") != capabilities.end() ? "true" : "false",
            std::find(capabilities.begin(), capabilities.end(), "fuel.write") != capabilities.end() ? "true" : "false",
            g_fuelReadDiagnostic.empty() ? (g_fuelReadOnlyReason.empty() ? "no diagnostic" : g_fuelReadOnlyReason.c_str()) : g_fuelReadDiagnostic.c_str());
        SendResponse(requestId, action, "Success", nullptr, nullptr, 0, 0, &capabilities, &writeMethods);
    } else if (action == "FINISH_FUEL_OPERATION") {
        const bool finished = FinishA32nxRefuel();
        std::fprintf(stderr, "[VTSD AAR Bridge] Fuel operation cleanup %s.\n", finished ? "succeeded" : "failed");
        SendResponse(requestId, action, finished ? "Success" : "Failed", finished ? nullptr : "Could not restore the A32NX refuel controller state.");
    } else if (action == "GET_FUEL_STATE") {
        FuelState state{};
        if (!ReadFuelState(state, false)) {
            std::fprintf(stderr, "[VTSD AAR Bridge] GET_FUEL_STATE failed: %s.\n", g_fuelReadDiagnostic.c_str());
            SendResponse(requestId, action, "Failed", g_fuelReadDiagnostic.c_str());
        }
        else {
            const auto writeMethods = GetWriteMethods(state);
            SendResponse(requestId, action, "Success", nullptr, &state, 0, 0, nullptr, &writeMethods);
        }
    } else {
        SendResponse(requestId, action, "Failed", "Unsupported bridge action.");
    }
}

} // namespace

extern "C" MSFS_CALLBACK void module_init(void) {
    g_writeProbeSignature.clear();
    g_newFuelSystem = fsVarsGetAVarId("NEW FUEL SYSTEM");
    g_tankQuantity = fsVarsGetAVarId("FUELSYSTEM TANK QUANTITY");
    g_tankCapacity = fsVarsGetAVarId("FUELSYSTEM TANK USABLE CAPACITY");
    g_fuelWeightPerGallon = fsVarsGetAVarId("FUEL WEIGHT PER GALLON");
    g_legacyTotalQuantity = fsVarsGetAVarId("FUEL TOTAL QUANTITY");
    g_legacyTotalCapacity = fsVarsGetAVarId("FUEL TOTAL CAPACITY");
    for (auto& tank : g_legacyTankDefinitions) {
        tank.quantityVar = fsVarsGetAVarId(tank.quantityName);
        tank.capacityVar = fsVarsGetAVarId(tank.capacityName);
    }
    g_boolUnit = fsVarsGetUnitId("Bool");
    g_numberUnit = fsVarsGetUnitId("Number");
    g_gallonsUnit = fsVarsGetUnitId("Gallons");
    g_poundsPerGallonUnit = fsVarsGetUnitId("Pounds per gallon");
    g_registered = fsCommBusRegister(kRequestEvent, OnRequest, nullptr);
    std::fprintf(stderr, "[VTSD AAR Bridge] Request listener registration %s.\n", g_registered ? "succeeded" : "FAILED");
}

extern "C" MSFS_CALLBACK void module_deinit(void) {
    FinishA32nxRefuel();
    if (g_registered) fsCommBusUnregisterOneEvent(kRequestEvent, OnRequest, nullptr);
    g_registered = false;
}
