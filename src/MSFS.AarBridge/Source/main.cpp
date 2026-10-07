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
#include <deque>
#include <cstdio>
#include <utility>
#include <string>
#include <vector>

namespace {

constexpr char kRequestEvent[] = "VTSD_AAR_REQUEST";
constexpr char kResponseEvent[] = "VTSD_AAR_RESPONSE";
constexpr char kBridgeVersion[] = "1.0.0";
constexpr int kProtocolVersion = 1;
constexpr int kMaximumTankIndex = 64;
constexpr size_t kMaximumCachedMutations = 256;
constexpr std::time_t kMutationCacheTtlSeconds = 24 * 60 * 60;
constexpr double kPoundsToKg = 0.45359237;
constexpr double kEpsilon = 0.0001;

struct Tank {
    int index;
    double quantityGallons;
    double capacityGallons;
    bool writable;
};

struct FuelState {
    double kgPerGallon;
    double totalKg;
    double capacityKg;
    std::vector<Tank> tanks;
    bool writable;
};

struct CachedMutation {
    std::string requestId;
    std::string action;
    int protocolVersion;
    double deltaKg;
    std::string response;
    std::time_t createdAt;
};

FsAVarId g_newFuelSystem = 0;
FsAVarId g_tankQuantity = 0;
FsAVarId g_tankCapacity = 0;
FsAVarId g_fuelWeightPerGallon = 0;
FsUnitId g_boolUnit = 0;
FsUnitId g_gallonsUnit = 0;
FsUnitId g_poundsPerGallonUnit = 0;
bool g_registered = false;
std::vector<std::pair<int, double>> g_writeProbeSignature;
std::deque<CachedMutation> g_mutationCache;
std::string g_fuelReadOnlyReason = "fuel capability has not been probed";
std::vector<std::string> g_writeProbeDiagnostics;

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

bool ReadVar(FsAVarId id, FsUnitId unit, const FsVarParamArray& params, double& value) {
    if (id == 0 || unit == 0) return false;
    return fsVarsAVarGet(id, unit, params, &value, FS_OBJECT_ID_USER_AIRCRAFT) == FS_VAR_ERROR_NONE && std::isfinite(value);
}

bool ReadFuelState(FuelState& result, bool probeWrites) {
    if (probeWrites) { g_writeProbeSignature.clear(); g_writeProbeDiagnostics.clear(); g_fuelReadOnlyReason.clear(); }
    const FsVarParamArray noParams{};
    double modernFuelSystem = 0;
    if (!ReadVar(g_newFuelSystem, g_boolUnit, noParams, modernFuelSystem) || modernFuelSystem < 0.5) {
        g_writeProbeSignature.clear();
        if (probeWrites) g_fuelReadOnlyReason = "no modern fuel system detected or NEW FUEL SYSTEM read failed";
        return false;
    }

    double poundsPerGallon = 0;
    if (!ReadVar(g_fuelWeightPerGallon, g_poundsPerGallonUnit, noParams, poundsPerGallon) || poundsPerGallon <= 0) {
        g_writeProbeSignature.clear();
        if (probeWrites) g_fuelReadOnlyReason = "fuel weight per gallon unavailable";
        return false;
    }

    result = {};
    result.kgPerGallon = poundsPerGallon * kPoundsToKg;

    bool foundTank = false;
    for (int index = 1; index <= kMaximumTankIndex; ++index) {
        ParamArray params(index);
        double capacityGallons = 0;
        double quantityGallons = 0;
        const bool hasCapacity = ReadVar(g_tankCapacity, g_gallonsUnit, params.value, capacityGallons);
        const bool hasQuantity = ReadVar(g_tankQuantity, g_gallonsUnit, params.value, quantityGallons);
        if (!hasCapacity || !hasQuantity) {
            if (!foundTank) {
                g_writeProbeSignature.clear();
                if (probeWrites) g_fuelReadOnlyReason = "first tank quantity/capacity read failed or no tanks discovered";
                return false;
            }
            break;
        }
        if (capacityGallons <= kEpsilon) {
            if (!foundTank) {
                g_writeProbeSignature.clear();
                return false;
            }
            break;
        }
        if (quantityGallons < -kEpsilon || quantityGallons > capacityGallons + 0.05) {
            g_writeProbeSignature.clear();
            return false;
        }

        bool writable = false;
        auto setterResult = FS_VAR_ERROR_NONE;
        double readBack = quantityGallons;
        if (probeWrites) setterResult = fsVarsAVarSet(g_tankQuantity, g_gallonsUnit, params.value, quantityGallons, FS_OBJECT_ID_USER_AIRCRAFT);
        if (probeWrites && setterResult == FS_VAR_ERROR_NONE) {
            writable = ReadVar(g_tankQuantity, g_gallonsUnit, params.value, readBack) && std::abs(readBack - quantityGallons) <= 0.001;
        }
        if (probeWrites) {
            char diagnostic[192]{};
            std::snprintf(diagnostic, sizeof(diagnostic), "tank=%d beforeGal=%.4f setResult=%d readBackGal=%.4f differenceGal=%.4f writable=%s",
                index, quantityGallons, static_cast<int>(setterResult), readBack, std::abs(readBack - quantityGallons), writable ? "true" : "false");
            g_writeProbeDiagnostics.emplace_back(diagnostic);
            if (!writable && g_fuelReadOnlyReason.empty()) g_fuelReadOnlyReason = setterResult != FS_VAR_ERROR_NONE
                ? "AVar setter returned error for one or more tanks"
                : "same-value write probe read-back differed or failed";
        }

        result.tanks.push_back({ index, std::max(0.0, quantityGallons), capacityGallons, writable });
        result.totalKg += std::max(0.0, quantityGallons) * result.kgPerGallon;
        result.capacityKg += capacityGallons * result.kgPerGallon;
        foundTank = true;
    }

    result.writable = foundTank && result.tanks.size() < static_cast<size_t>(kMaximumTankIndex);
    for (const auto& tank : result.tanks) result.writable = result.writable && tank.writable;

    if (probeWrites) {
        g_writeProbeSignature.clear();
        if (result.writable) {
            for (const auto& tank : result.tanks) g_writeProbeSignature.emplace_back(tank.index, tank.capacityGallons);
        }
    } else if (result.tanks.size() == g_writeProbeSignature.size() && !g_writeProbeSignature.empty()) {
        bool sameStructure = true;
        for (size_t i = 0; i < result.tanks.size(); ++i) {
            sameStructure = sameStructure && result.tanks[i].index == g_writeProbeSignature[i].first &&
                std::abs(result.tanks[i].capacityGallons - g_writeProbeSignature[i].second) <= 0.01;
        }
        if (sameStructure) {
            for (auto& tank : result.tanks) tank.writable = true;
            result.writable = true;
        } else {
            g_writeProbeSignature.clear();
            g_fuelReadOnlyReason = "tank topology changed after write probe; fuel.write revoked";
        }
    } else if (!g_writeProbeSignature.empty()) {
        g_writeProbeSignature.clear();
        g_fuelReadOnlyReason = "tank topology changed after write probe; fuel.write revoked";
    }
    if (!foundTank || result.capacityKg <= 0) {
        g_writeProbeSignature.clear();
        if (probeWrites && g_fuelReadOnlyReason.empty()) g_fuelReadOnlyReason = "no usable tanks discovered";
        return false;
    }
    if (probeWrites && !result.writable && g_fuelReadOnlyReason.empty()) g_fuelReadOnlyReason = "one or more discovered tanks failed the write probe or tank scan limit";
    return true;
}

std::string MakeResponse(const std::string& requestId, const std::string& action, const char* status,
    const char* error = nullptr, const FuelState* fuel = nullptr, double requestedKg = 0, double appliedKg = 0) {
    rapidjson::Document document(rapidjson::kObjectType);
    auto& allocator = document.GetAllocator();
    document.AddMember("requestId", rapidjson::Value(requestId.c_str(), allocator), allocator);
    document.AddMember("action", rapidjson::Value(action.c_str(), allocator), allocator);
    document.AddMember("protocolVersion", kProtocolVersion, allocator);
    document.AddMember("bridgeVersion", rapidjson::Value(kBridgeVersion, allocator), allocator);
    document.AddMember("status", rapidjson::Value(status, allocator), allocator);
    if (error != nullptr) document.AddMember("error", rapidjson::Value(error, allocator), allocator);

    if (action == "GET_CAPABILITIES") {
        rapidjson::Value capabilities(rapidjson::kArrayType);
        FuelState probe{};
        if (ReadFuelState(probe, false)) capabilities.PushBack("fuel.read", allocator);
        if (ReadFuelState(probe, true) && probe.writable) capabilities.PushBack("fuel.write", allocator);
        document.AddMember("capabilities", capabilities, allocator);
        rapidjson::Value diagnostics(rapidjson::kObjectType);
        diagnostics.AddMember("fuelReadOnlyReason", rapidjson::Value(g_fuelReadOnlyReason.c_str(), allocator), allocator);
        rapidjson::Value probes(rapidjson::kArrayType);
        for (const auto& line : g_writeProbeDiagnostics) probes.PushBack(rapidjson::Value(line.c_str(), allocator), allocator);
        diagnostics.AddMember("writeProbe", probes, allocator);
        document.AddMember("diagnostics", diagnostics, allocator);
    }
    else if (!g_fuelReadOnlyReason.empty() || !g_writeProbeDiagnostics.empty()) {
        rapidjson::Value diagnostics(rapidjson::kObjectType);
        diagnostics.AddMember("fuelReadOnlyReason", rapidjson::Value(g_fuelReadOnlyReason.c_str(), allocator), allocator);
        rapidjson::Value probes(rapidjson::kArrayType);
        for (const auto& line : g_writeProbeDiagnostics) probes.PushBack(rapidjson::Value(line.c_str(), allocator), allocator);
        diagnostics.AddMember("writeProbe", probes, allocator);
        document.AddMember("diagnostics", diagnostics, allocator);
    }
    if (fuel != nullptr) {
        rapidjson::Value fuelState(rapidjson::kObjectType);
        const auto sampledAtUtc = TimestampUtc();
        fuelState.AddMember("sampledAtUtc", rapidjson::Value(sampledAtUtc.c_str(), allocator), allocator);
        fuelState.AddMember("currentFuelKg", fuel->totalKg, allocator);
        fuelState.AddMember("capacityKg", fuel->capacityKg, allocator);
        fuelState.AddMember("fuelWeightPerGallonLb", fuel->kgPerGallon / kPoundsToKg, allocator);
        rapidjson::Value tanks(rapidjson::kArrayType);
        for (const auto& tank : fuel->tanks) {
            rapidjson::Value item(rapidjson::kObjectType);
            const auto tankId = std::string("tank-") + std::to_string(tank.index);
            item.AddMember("tankId", rapidjson::Value(tankId.c_str(), allocator), allocator);
            item.AddMember("currentKg", tank.quantityGallons * fuel->kgPerGallon, allocator);
            item.AddMember("capacityKg", tank.capacityGallons * fuel->kgPerGallon, allocator);
            item.AddMember("writable", tank.writable, allocator);
            tanks.PushBack(item, allocator);
        }
        fuelState.AddMember("tanks", tanks, allocator);
        document.AddMember("fuelState", fuelState, allocator);
    }
    if (action == "APPLY_FUEL_DELTA") {
        document.AddMember("requestedKg", requestedKg, allocator);
        document.AddMember("appliedKg", appliedKg, allocator);
    }

    rapidjson::StringBuffer buffer;
    rapidjson::Writer<rapidjson::StringBuffer> writer(buffer);
    document.Accept(writer);
    return std::string(buffer.GetString(), buffer.GetSize());
}

void SendResponse(const std::string& payload) {
    fsCommBusCall(kResponseEvent, payload.c_str(), static_cast<unsigned int>(payload.size()), FsCommBusBroadcast_SimConnect);
}

std::string ApplyFuelDelta(const std::string& requestId, double requestedKg) {
    if (!std::isfinite(requestedKg) || std::abs(requestedKg) < kEpsilon) {
        return MakeResponse(requestId, "APPLY_FUEL_DELTA", "Failed", "Fuel delta must be finite and non-zero.", nullptr, requestedKg);
    }

    FuelState before{};
    if (!ReadFuelState(before, true) || !before.writable) {
        return MakeResponse(requestId, "APPLY_FUEL_DELTA", "Failed", "This aircraft fuel system is not safely writable.", nullptr, requestedKg);
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
    double remainder = targetGallons;
    bool allWritesSucceeded = targetGallons > kEpsilon;
    size_t finalWeightedIndex = before.tanks.size();
    for (size_t i = 0; i < weights.size(); ++i) if (weights[i] > kEpsilon) finalWeightedIndex = i;
    for (size_t i = 0; i < before.tanks.size() && allWritesSucceeded; ++i) {
        if (weights[i] <= kEpsilon) continue;
        const double allocation = i == finalWeightedIndex
            ? remainder
            : std::min(remainder, targetGallons * weights[i] / totalWeight);
        remainder -= allocation;
        const auto& tank = before.tanks[i];
        const double sign = requestedKg > 0 ? 1.0 : -1.0;
        const double nextQuantity = std::max(0.0, std::min(tank.quantityGallons + sign * allocation, tank.capacityGallons));
        ParamArray params(tank.index);
        allWritesSucceeded = fsVarsAVarSet(g_tankQuantity, g_gallonsUnit, params.value, nextQuantity, FS_OBJECT_ID_USER_AIRCRAFT) == FS_VAR_ERROR_NONE;
        double readBack = 0;
        allWritesSucceeded = allWritesSucceeded && ReadVar(g_tankQuantity, g_gallonsUnit, params.value, readBack) && std::abs(readBack - nextQuantity) <= 0.01;
    }

    FuelState after{};
    if (!ReadFuelState(after, false)) {
        return MakeResponse(requestId, "APPLY_FUEL_DELTA", "Failed", "Fuel mutation could not be verified by simulator read-back.", nullptr, requestedKg);
    }

    const double signedAppliedKg = after.totalKg - before.totalKg;
    const double appliedKg = std::abs(signedAppliedKg);
    if (!std::isfinite(signedAppliedKg) || std::abs(signedAppliedKg) > std::abs(requestedKg) + 0.05 ||
        (appliedKg > 0.01 && std::signbit(signedAppliedKg) != std::signbit(requestedKg))) {
        return MakeResponse(requestId, "APPLY_FUEL_DELTA", "Failed", "Simulator read-back did not match the requested direction or bound.", &after, requestedKg);
    }

    const bool complete = allWritesSucceeded && std::abs(signedAppliedKg - requestedKg) <= 0.05;
    const char* status = complete ? "Success" : appliedKg > 0.01 ? "Partial" : "Failed";
    return MakeResponse(requestId, "APPLY_FUEL_DELTA", status,
        complete ? nullptr : "Fuel transfer was partial or a tank write failed; further transfer must stop.", &after, requestedKg, signedAppliedKg);
}

void PruneMutationCache(std::time_t now) {
    while (!g_mutationCache.empty() && now - g_mutationCache.front().createdAt > kMutationCacheTtlSeconds)
        g_mutationCache.pop_front();
    while (g_mutationCache.size() > kMaximumCachedMutations) g_mutationCache.pop_front();
}

void CacheMutation(const std::string& requestId, const std::string& action, int protocolVersion, double deltaKg, const std::string& response) {
    const auto now = std::time(nullptr);
    PruneMutationCache(now);
    g_mutationCache.push_back({ requestId, action, protocolVersion, deltaKg, response, now });
    while (g_mutationCache.size() > kMaximumCachedMutations) g_mutationCache.pop_front();
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

    if (action == "APPLY_FUEL_DELTA") {
        const auto deltaValue = request.FindMember("deltaKg");
        const auto protocolValue = request.FindMember("protocolVersion");
        if (deltaValue == request.MemberEnd() || !deltaValue->value.IsNumber() ||
            protocolValue == request.MemberEnd() || !protocolValue->value.IsInt()) {
            SendResponse(MakeResponse(requestId, action, "Failed", "APPLY_FUEL_DELTA requires protocolVersion and numeric deltaKg."));
            return;
        }
        const int protocolVersion = protocolValue->value.GetInt();
        if (protocolVersion != kProtocolVersion) {
            SendResponse(MakeResponse(requestId, action, "Failed", "Bridge protocol version mismatch."));
            return;
        }

        const double deltaKg = deltaValue->value.GetDouble();
        const auto now = std::time(nullptr);
        PruneMutationCache(now);
        const auto cached = std::find_if(g_mutationCache.begin(), g_mutationCache.end(), [&](const CachedMutation& entry) {
            return entry.requestId == requestId;
        });
        if (cached != g_mutationCache.end()) {
            if (cached->action == action && cached->protocolVersion == protocolVersion && cached->deltaKg == deltaKg) SendResponse(cached->response);
            else SendResponse(MakeResponse(requestId, action, "Failed", "IDEMPOTENCY_CONFLICT: requestId was already used for a different fuel mutation."));
            return;
        }

        const auto response = ApplyFuelDelta(requestId, deltaKg);
        CacheMutation(requestId, action, protocolVersion, deltaKg, response);
        SendResponse(response);
        return;
    }

    const auto protocolValue = request.FindMember("protocolVersion");
    if (action != "HELLO" && protocolValue != request.MemberEnd() && protocolValue->value.IsInt() &&
        protocolValue->value.GetInt() != kProtocolVersion) {
        SendResponse(MakeResponse(requestId, action, "Failed", "Bridge protocol version mismatch."));
        return;
    }

    if (action == "HELLO") {
        SendResponse(MakeResponse(requestId, action, "Success"));
    } else if (action == "GET_CAPABILITIES") {
        SendResponse(MakeResponse(requestId, action, "Success"));
    } else if (action == "GET_FUEL_STATE") {
        FuelState state{};
        if (!ReadFuelState(state, false)) SendResponse(MakeResponse(requestId, action, "Failed", "MSFS 2024 fuel state is unavailable."));
        else SendResponse(MakeResponse(requestId, action, "Success", nullptr, &state));
    } else {
        SendResponse(MakeResponse(requestId, action, "Failed", "Unsupported bridge action."));
    }
}

} // namespace

extern "C" MSFS_CALLBACK void module_init(void) {
    g_mutationCache.clear();
    g_writeProbeSignature.clear();
    g_newFuelSystem = fsVarsGetAVarId("NEW FUEL SYSTEM");
    g_tankQuantity = fsVarsGetAVarId("FUELSYSTEM TANK QUANTITY");
    g_tankCapacity = fsVarsGetAVarId("FUELSYSTEM TANK USABLE CAPACITY");
    g_fuelWeightPerGallon = fsVarsGetAVarId("FUEL WEIGHT PER GALLON");
    g_boolUnit = fsVarsGetUnitId("Bool");
    g_gallonsUnit = fsVarsGetUnitId("Gallons");
    g_poundsPerGallonUnit = fsVarsGetUnitId("Pounds per gallon");
    g_registered = fsCommBusRegister(kRequestEvent, OnRequest, nullptr);
}

extern "C" MSFS_CALLBACK void module_deinit(void) {
    if (g_registered) fsCommBusUnregisterOneEvent(kRequestEvent, OnRequest, nullptr);
    g_registered = false;
}
