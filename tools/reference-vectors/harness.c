/*
 * Reference-vector harness.
 *
 * Compiles Kinetic's ORIGINAL inRide.c (sensors-swift-kinetic @ 9335f10, SDK 2.3.4)
 * unmodified, and reproduces the small amount of plain-C logic that lives in
 * KineticInRide.m (cadence rolling average + the double->uint16 cadence truncation)
 * so that the C# port can be checked against the real implementation.
 *
 * This is a development tool only. It is never shipped with the application.
 *
 * stdin commands (one per line), stdout = one JSON object per line:
 *   R                         reset cadence buffer to a fresh-process state
 *   P <40 hex> <seconds>      processPowerData (C parse + ObjC cadence adjust)
 *   C <40 hex>                processConfigurationData
 *   K <12 hex> <rate>         configureSensorCommandData (rate = 32/16/8)
 *   S <12 hex>                startCalibrationCommandData
 *   T <12 hex>                stopCalibrationCommandData
 *   D <12 hex> <seconds>      setSpindownTimeCommandData
 */
#include <stdio.h>
#include <string.h>
#include <stdint.h>
#include "inRide.c"

/* ---- Verbatim logic from KineticInRide.m (adjustCadence), expressed in C ---- */
typedef struct { double timestamp; uint16_t cadenceRPM; } cadence;
#define CADENCE_BUFFER_SIZE_MAX 10
#define CADENCE_BUFFER_SIZE_DEFAULT 3
#define CADENCE_BUFFER_WEIGHT_DEFAULT 2
static unsigned long cadenceBufferSize = CADENCE_BUFFER_SIZE_DEFAULT;
static unsigned long cadenceBufferWeight = CADENCE_BUFFER_WEIGHT_DEFAULT;
static unsigned long cadenceBufferCount = 0;
static cadence cadenceBuffer[CADENCE_BUFFER_SIZE_MAX];

static double adjustCadence(uint16_t crankRPM, double timestamp)
{
    if (crankRPM == 0) {
        cadenceBufferCount = 0;
        return 0;
    }
    if (cadenceBufferCount > 0 && timestamp - cadenceBuffer[0].timestamp > 2) {
        cadenceBufferCount = 0;
    }
    for (unsigned long i = cadenceBufferCount; i > 0; i--) {
        cadenceBuffer[i].timestamp = cadenceBuffer[i - 1].timestamp;
        cadenceBuffer[i].cadenceRPM = cadenceBuffer[i - 1].cadenceRPM;
    }
    cadenceBuffer[0].cadenceRPM = crankRPM;
    cadenceBuffer[0].timestamp = timestamp;
    cadenceBufferCount = (cadenceBufferSize < cadenceBufferCount + 1) ? cadenceBufferSize : cadenceBufferCount + 1;
    double rollingRPM = crankRPM * cadenceBufferWeight;
    for (int i = 1; i < cadenceBufferSize; i++) {
        rollingRPM += cadenceBuffer[i].cadenceRPM;
    }
    rollingRPM /= cadenceBufferSize + cadenceBufferWeight - 1;
    return rollingRPM;
}
/* --------------------------------------------------------------------------- */

static int parse_hex(const char *hex, uint8_t *out, int n)
{
    if ((int)strlen(hex) != n * 2) return 0;
    for (int i = 0; i < n; i++) {
        unsigned v;
        if (sscanf(hex + 2 * i, "%2x", &v) != 1) return 0;
        out[i] = (uint8_t)v;
    }
    return 1;
}

static void print_bytes(const uint8_t *b, size_t n)
{
    for (size_t i = 0; i < n; i++) printf("%02x", b[i]);
}

int main(void)
{
    char line[512];
    setvbuf(stdout, NULL, _IOFBF, 1 << 16);
    while (fgets(line, sizeof line, stdin)) {
        char cmd = 0, a[128] = {0};
        double num = 0;
        int got = sscanf(line, " %c %127s %lf", &cmd, a, &num);
        if (got < 1) continue;
        if (cmd == 'R') {
            memset(cadenceBuffer, 0, sizeof cadenceBuffer);
            cadenceBufferCount = 0;
            printf("{\"op\":\"R\"}\n");
        } else if (cmd == 'P') {
            uint8_t d[20];
            if (!parse_hex(a, d, 20)) { printf("{\"op\":\"P\",\"error\":\"bad hex\"}\n"); continue; }
            inride_power_data p = inride_process_power_data(d);
            uint16_t cadenceRaw = (uint16_t)p.cadenceRPM;          /* powerData.cadenceRaw = cData.cadenceRPM; */
            double cadenceAdjusted = adjustCadence(cadenceRaw, num);
            printf("{\"op\":\"P\",\"in\":\"%s\",\"t\":%.17g,\"state\":%d,\"commandResult\":%d,\"power\":%d,"
                   "\"speedKPH\":%.17g,\"rollerRPM\":%.17g,\"cadenceC\":%.17g,\"cadenceRaw\":%u,\"cadenceRPM\":%.17g,"
                   "\"coasting\":%s,\"spindownTime\":%.17g,\"lastSpindownResultTime\":%.17g,\"rollerResistance\":%.17g,"
                   "\"proFlywheel\":%s,\"calibrationResult\":%d}\n",
                   a, num, (int)p.state, (int)p.commandResult, p.power, p.speedKPH, p.rollerRPM, p.cadenceRPM,
                   (unsigned)cadenceRaw, cadenceAdjusted, p.coasting ? "true" : "false", p.spindownTime,
                   p.lastSpindownResultTime, p.rollerResistance, p.proFlywheel ? "true" : "false",
                   (int)p.calibrationResult);
        } else if (cmd == 'C') {
            uint8_t d[20];
            if (!parse_hex(a, d, 20)) { printf("{\"op\":\"C\",\"error\":\"bad hex\"}\n"); continue; }
            inride_config_data c = inride_process_config_data(d);
            printf("{\"op\":\"C\",\"in\":\"%s\",\"calibrationReady\":%u,\"calibrationStart\":%u,\"calibrationEnd\":%u,"
                   "\"calibrationDebounce\":%u,\"currentSpindownTime\":%.17g,\"updateRateDefault\":%u,"
                   "\"updateRateCalibration\":%u,\"proFlywheel\":%s}\n",
                   a, c.calibrationReady, c.calibrationStart, c.calibrationEnd, c.calibrationDebounce,
                   c.currentSpindownTime, c.updateRateDefault, c.updateRateCalibration,
                   c.proFlywheel ? "true" : "false");
        } else if (cmd == 'K' || cmd == 'S' || cmd == 'T' || cmd == 'D') {
            uint8_t s[6];
            if (!parse_hex(a, s, 6)) { printf("{\"op\":\"%c\",\"error\":\"bad hex\"}\n", cmd); continue; }
            printf("{\"op\":\"%c\",\"systemId\":\"%s\",\"arg\":%.17g,\"out\":\"", cmd, a, num);
            if (cmd == 'K') {
                inride_config_sensor_command k = inride_create_config_sensor_command_data((inride_update_rate)(int)num, s);
                print_bytes((uint8_t *)&k, sizeof k);
            } else if (cmd == 'S') {
                inride_start_calibration_command k = inride_create_start_calibration_command_data(s);
                print_bytes((uint8_t *)&k, sizeof k);
            } else if (cmd == 'T') {
                inride_stop_calibration_command k = inride_create_stop_calibration_command_data(s);
                print_bytes((uint8_t *)&k, sizeof k);
            } else {
                inride_set_spindown_time_command k = inride_create_set_spindown_time_command_data(num, s);
                print_bytes((uint8_t *)&k, sizeof k);
            }
            printf("\"}\n");
        }
    }
    return 0;
}
