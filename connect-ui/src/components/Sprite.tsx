import { useEffect, useState } from 'react';
import { api } from '../api/client';
import type { SpriteSet } from '../api/types';

// Each set's picture, cut out and trimmed, made once per page load.
const cache = new Map<string, Promise<string | undefined>>();

/**
 * The games' sprites sit on a flat backdrop the game never shows, and a set's image may be a
 * whole sheet. The frame to show is cut out, the backdrop (the colour two or more corners share)
 * is cleared from the edges inwards, and the result is trimmed to what is drawn, so sprites of
 * different sources all fill the same box alike.
 */
function prepare(url: string, frame: SpriteSet['frame']): Promise<string | undefined> {
  return new Promise((resolve) => {
    const image = new Image();
    image.onerror = () => resolve(undefined);
    image.onload = () => {
      const [sx, sy, sw, sh] = frame ?? [0, 0, image.naturalWidth, image.naturalHeight];
      const canvas = document.createElement('canvas');
      canvas.width = sw;
      canvas.height = sh;
      const context = canvas.getContext('2d');
      if (!context || sw <= 0 || sh <= 0) return resolve(url);
      context.imageSmoothingEnabled = false;
      context.drawImage(image, sx, sy, sw, sh, 0, 0, sw, sh);

      const pixels = context.getImageData(0, 0, sw, sh);
      const px = pixels.data;
      const near = (a: number, b: number, tolerance: number) =>
        Math.abs(px[a]! - px[b]!) + Math.abs(px[a + 1]! - px[b + 1]!) + Math.abs(px[a + 2]! - px[b + 2]!) <= tolerance;
      const corners = [0, sw - 1, (sh - 1) * sw, (sh - 1) * sw + sw - 1].map((p) => p * 4);
      const backdrop = corners.find((c) => px[c + 3]! > 200 && corners.filter((o) => px[o + 3]! > 200 && near(o, c, 42)).length >= 2);

      if (backdrop !== undefined) {
        const [r, g, b] = [px[backdrop]!, px[backdrop + 1]!, px[backdrop + 2]!];
        const isBackdrop = (i: number) => px[i + 3]! > 0 && Math.abs(px[i]! - r) + Math.abs(px[i + 1]! - g) + Math.abs(px[i + 2]! - b) <= 42;
        const seen = new Uint8Array(sw * sh);
        const stack: number[] = [];
        for (let x = 0; x < sw; x++) stack.push(x, (sh - 1) * sw + x);
        for (let y = 0; y < sh; y++) stack.push(y * sw, y * sw + sw - 1);
        while (stack.length > 0) {
          const p = stack.pop()!;
          if (seen[p]) continue;
          seen[p] = 1;
          if (!isBackdrop(p * 4)) continue;
          px[p * 4 + 3] = 0;
          const x = p % sw;
          if (x + 1 < sw) stack.push(p + 1);
          if (x > 0) stack.push(p - 1);
          if (p + sw < sw * sh) stack.push(p + sw);
          if (p - sw >= 0) stack.push(p - sw);
        }
        context.putImageData(pixels, 0, 0);
      }

      // Trim to what is drawn.
      let [minX, minY, maxX, maxY] = [sw, sh, -1, -1];
      for (let y = 0; y < sh; y++)
        for (let x = 0; x < sw; x++)
          if (px[(y * sw + x) * 4 + 3]! > 0) {
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
          }
      if (maxX < 0) return resolve(canvas.toDataURL());

      const trimmed = document.createElement('canvas');
      trimmed.width = maxX - minX + 1;
      trimmed.height = maxY - minY + 1;
      trimmed.getContext('2d')!.drawImage(canvas, minX, minY, trimmed.width, trimmed.height, 0, 0, trimmed.width, trimmed.height);
      resolve(trimmed.toDataURL());
    };
    image.src = url;
  });
}

/** A sprite set's picture, fitted into a square box of the given size. */
export function Sprite({ set, size, title }: { set: SpriteSet | undefined; size: number; title?: string }) {
  const [source, setSource] = useState<string>();

  useEffect(() => {
    let current = true;
    setSource(undefined);
    if (!set) return;
    let pending = cache.get(set.id);
    if (!pending) {
      pending = prepare(api.spriteUrl(set.id), set.frame);
      cache.set(set.id, pending);
    }
    pending.then((url) => current && setSource(url));
    return () => {
      current = false;
    };
  }, [set]);

  return (
    <span className="spritebox" style={{ width: size, height: size }} title={title}>
      {source && <img src={source} alt="" />}
    </span>
  );
}