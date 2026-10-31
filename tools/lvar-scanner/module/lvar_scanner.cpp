// Crosscheck LVar scanner: a minimal MSFS 2024 standalone WASM module that lists every L: variable
// with its current value and hands the list to an external SimConnect client via client data.
//
// STRICTLY READ-ONLY: this module only calls fsVarsGetLVarName and fsVarsLVarGet. It never sets,
// registers or creates an L: var (build.ps1 refuses to build if a write/register call appears).
//
// Protocol (text, NUL-terminated):
//   client -> CROSSCHECK.LVARSCAN.CMD    "PING <seq>" | "SCAN <seq>" | "ACK <seq> <chunk>"
//   module -> CROSSCHECK.LVARSCAN.DATA   "PONG <seq> <moduleVersion> <lvarCount>\n"
//                                        "CHUNK <seq> <index> <total> <count>\n" + records
//   record: "<id>\t<name>\t<value>\n"

#include <MSFS/MSFS.h>
#include <MSFS/MSFS_WindowsTypes.h>
#include <MSFS/MSFS_Vars.h>
#include <SimConnect.h>

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static const char* const ModuleVersion = "0.1.0";
static const DWORD CmdSize = 256;
static const DWORD DataSize = 8000;
static const int MaxId = 200000;
static const int MaxConsecutiveMisses = 2000;

enum ClientDataId { CD_CMD = 0, CD_DATA = 1 };
enum DefineId { DEF_CMD = 0, DEF_DATA = 1 };
enum RequestId { REQ_CMD = 0 };

static HANDLE sim = 0;
static FsUnitId numberUnit = -1;

// The current snapshot: all records in one buffer, plus the offsets where each chunk starts.
static char* snapshot = nullptr;
static size_t snapshotLen = 0, snapshotCap = 0;
static int* chunkStarts = nullptr;
static int chunkCount = 0, recordCount = 0;
static unsigned snapshotSeq = 0;
static char out[DataSize];

static void Append(const char* s, size_t n)
{
    if (snapshotLen + n > snapshotCap)
    {
        size_t cap = snapshotCap ? snapshotCap * 2 : 256 * 1024;
        while (cap < snapshotLen + n) cap *= 2;
        char* grown = (char*)realloc(snapshot, cap);
        if (!grown) return;
        snapshot = grown;
        snapshotCap = cap;
    }
    memcpy(snapshot + snapshotLen, s, n);
    snapshotLen += n;
}

static void Send(const char* text)
{
    memset(out, 0, sizeof(out));
    strncpy(out, text, sizeof(out) - 1);
    SimConnect_SetClientData(sim, CD_DATA, DEF_DATA, SIMCONNECT_CLIENT_DATA_SET_FLAG_DEFAULT, 0, DataSize, out);
}

// Calls visit(id, name) for every L: var id that currently has a name.
template <typename Visit>
static int ForEachLVar(Visit visit)
{
    char name[256];
    int found = 0, misses = 0;
    for (FsLVarId id = 0; id < MaxId && misses < MaxConsecutiveMisses; id++)
    {
        name[0] = '\0';
        if (fsVarsGetLVarName(id, name, sizeof(name)) == FS_VAR_ERROR_NONE && name[0] != '\0')
        {
            misses = 0;
            found++;
            visit(id, name);
        }
        else
        {
            misses++;
        }
    }
    return found;
}

static void TakeSnapshot(unsigned seq)
{
    snapshotLen = 0;
    recordCount = 0;
    free(chunkStarts);
    chunkStarts = nullptr;
    int chunkCap = 0;
    chunkCount = 0;
    const size_t maxPayload = DataSize - 64; // room for the CHUNK header and the terminating NUL
    size_t chunkStart = 0;

    ForEachLVar([&](FsLVarId id, char* name) {
        for (char* p = name; *p; p++)
            if (*p == '\t' || *p == '\n' || *p == '\r') *p = '?';
        double value = 0;
        if (fsVarsLVarGet(id, numberUnit, &value) != FS_VAR_ERROR_NONE) value = 0;
        char record[320];
        int n = snprintf(record, sizeof(record), "%d\t%s\t%.17g\n", id, name, value);
        if (n <= 0 || n >= (int)sizeof(record)) return;
        if (chunkCount == 0 || snapshotLen + n - chunkStart > maxPayload)
        {
            if (chunkCount == chunkCap)
            {
                chunkCap = chunkCap ? chunkCap * 2 : 64;
                chunkStarts = (int*)realloc(chunkStarts, chunkCap * sizeof(int));
            }
            chunkStart = snapshotLen;
            chunkStarts[chunkCount++] = (int)snapshotLen;
        }
        Append(record, n);
        recordCount++;
    });
    snapshotSeq = seq;
}

static void SendChunk(int index)
{
    if (index < 0 || index >= chunkCount) return;
    size_t start = chunkStarts[index];
    size_t end = index + 1 < chunkCount ? chunkStarts[index + 1] : snapshotLen;
    int header = snprintf(out, sizeof(out), "CHUNK %u %d %d %d\n", snapshotSeq, index, chunkCount, recordCount);
    char text[DataSize];
    memcpy(text, out, header);
    memcpy(text + header, snapshot + start, end - start);
    text[header + (end - start)] = '\0';
    Send(text);
}

static void OnCommand(const char* cmd)
{
    unsigned seq = 0;
    int chunk = 0;
    char reply[128];
    if (sscanf(cmd, "PING %u", &seq) == 1)
    {
        int count = ForEachLVar([](FsLVarId, char*) {});
        snprintf(reply, sizeof(reply), "PONG %u %s %d\n", seq, ModuleVersion, count);
        Send(reply);
    }
    else if (sscanf(cmd, "SCAN %u", &seq) == 1)
    {
        TakeSnapshot(seq);
        if (chunkCount == 0)
        {
            snprintf(reply, sizeof(reply), "CHUNK %u 0 0 0\n", seq);
            Send(reply);
        }
        else
        {
            SendChunk(0);
        }
    }
    else if (sscanf(cmd, "ACK %u %d", &seq, &chunk) == 2 && seq == snapshotSeq)
    {
        SendChunk(chunk + 1);
    }
}

static void CALLBACK Dispatch(SIMCONNECT_RECV* data, DWORD, void*)
{
    if (data->dwID != SIMCONNECT_RECV_ID_CLIENT_DATA) return;
    auto* cd = (SIMCONNECT_RECV_CLIENT_DATA*)data;
    if (cd->dwRequestID != REQ_CMD) return;
    char cmd[CmdSize + 1];
    memcpy(cmd, &cd->dwData, CmdSize);
    cmd[CmdSize] = '\0';
    OnCommand(cmd);
}

extern "C"
{
    MSFS_CALLBACK void module_init(void)
    {
        numberUnit = fsVarsGetUnitId("number");
        if (FAILED(SimConnect_Open(&sim, "Crosscheck LVar Scanner", nullptr, 0, 0, 0)))
        {
            fprintf(stderr, "[Crosscheck LVar Scanner] SimConnect_Open failed\n");
            sim = 0;
            return;
        }
        SimConnect_MapClientDataNameToID(sim, "CROSSCHECK.LVARSCAN.CMD", CD_CMD);
        SimConnect_MapClientDataNameToID(sim, "CROSSCHECK.LVARSCAN.DATA", CD_DATA);
        SimConnect_CreateClientData(sim, CD_CMD, CmdSize, SIMCONNECT_CREATE_CLIENT_DATA_FLAG_DEFAULT);
        SimConnect_CreateClientData(sim, CD_DATA, DataSize, SIMCONNECT_CREATE_CLIENT_DATA_FLAG_DEFAULT);
        SimConnect_AddToClientDataDefinition(sim, DEF_CMD, 0, CmdSize);
        SimConnect_AddToClientDataDefinition(sim, DEF_DATA, 0, DataSize);
        SimConnect_RequestClientData(sim, CD_CMD, REQ_CMD, DEF_CMD,
            SIMCONNECT_CLIENT_DATA_PERIOD_ON_SET, SIMCONNECT_CLIENT_DATA_REQUEST_FLAG_DEFAULT);
        fprintf(stdout, "[Crosscheck LVar Scanner] %s ready\n", ModuleVersion);
    }

    MSFS_CALLBACK void module_post_sim_systems_update(float)
    {
        if (sim) SimConnect_CallDispatch(sim, Dispatch, nullptr);
    }

    MSFS_CALLBACK void module_deinit(void)
    {
        if (sim) SimConnect_Close(sim);
        sim = 0;
        free(snapshot);
        free(chunkStarts);
        snapshot = nullptr;
        chunkStarts = nullptr;
    }
}
