// AUTO-GENERATED decision tree. Feature order:
// 0:mq5_mean, 1:mq5_max, 2:mq5_std, 3:mq5_slope, 4:mq135_mean, 5:mq135_max, 6:mq135_std, 7:mq135_slope, 8:temp_mean, 9:temp_max, 10:temp_std, 11:temp_slope, 12:hum_mean, 13:hum_max, 14:hum_std, 15:hum_slope
// Output: 0=NORMAL 1=COOKING 2=GAS 3=FIRE
#pragma once
#include <stdint.h>

uint8_t run_tinyml_model(const float* features) {
    if (features[5] <= 1934.8470f) { // mq135_max
        if (features[12] <= 55.6464f) { // hum_mean
            if (features[1] <= 1491.5969f) { // mq5_max
                if (features[4] <= 1618.8992f) { // mq135_mean
                    return 0; // NORMAL
                } else {
                    return 2; // GAS
                }
            } else {
                if (features[1] <= 1518.2957f) { // mq5_max
                    return 2; // GAS
                } else {
                    return 2; // GAS
                }
            }
        } else {
            return 1; // COOKING
        }
    } else {
        return 3; // FIRE
    }
}
