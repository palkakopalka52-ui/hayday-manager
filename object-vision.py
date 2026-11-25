import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).with_name("vision-packages")))
import cv2
import numpy as np


def main():
    path, raw_x, raw_y, raw_width, raw_height, raw_radius, samples, *mode = sys.argv[1:]
    x, y, width, height, radius = map(int, (raw_x, raw_y, raw_width, raw_height, raw_radius))
    frame = cv2.imdecode(np.frombuffer(Path(path).read_bytes(), dtype=np.uint8), cv2.IMREAD_COLOR)
    if frame is None:
        return
    left, top = max(0, x - radius), max(0, y - radius)
    right = min(frame.shape[1], x + width + radius)
    bottom = min(frame.shape[0], y + height + radius)
    if right - left < width or bottom - top < height:
        return
    if mode in (["sickle"], ["stall"]):
        mask = cv2.imdecode(np.frombuffer(Path(__file__).with_name(mode[0] + "-mask.png").read_bytes(),
                                          dtype=np.uint8), cv2.IMREAD_GRAYSCALE)
        template = cv2.resize(mask, (width, height), interpolation=cv2.INTER_NEAREST)
        patch = frame[top:bottom, left:right]
        light = neutral_white(patch) if mode == ["sickle"] else striped_canopy(patch)
        scores = cv2.matchTemplate(light, template, cv2.TM_CCOEFF_NORMED)
        _, score, _, location = cv2.minMaxLoc(scores)
        if score >= 0.60:
            print(left + location[0], top + location[1])
        return
    rgb = np.fromstring(samples, sep=",", dtype=np.int32)
    if rgb.size != 64:
        return
    template = np.stack((rgb & 255, (rgb >> 8) & 255, rgb >> 16), axis=1).astype(np.uint8)
    template = cv2.resize(template.reshape(8, 8, 3), (width, height), interpolation=cv2.INTER_LINEAR)
    matches = cv2.matchTemplate(frame[top:bottom, left:right], template, cv2.TM_SQDIFF)
    _, _, location, _ = cv2.minMaxLoc(matches)
    print(left + location[0], top + location[1])


def neutral_white(image):
    lowest = image.min(axis=2)
    highest = image.max(axis=2)
    return ((lowest >= 145) & (highest - lowest <= 42)).astype(np.uint8) * 255


def striped_canopy(image):
    blue, green, red = image.astype(np.int16).transpose(2, 0, 1)
    white = (red > 175) & (green > 165) & (blue > 145) & (red - blue < 80)
    red_stripe = (red > 165) & (red - green > 70) & (red - blue > 70) & (green < 143)
    return np.where(white, 255, np.where(red_stripe, 115, 0)).astype(np.uint8)


if __name__ == "__main__":
    main()
