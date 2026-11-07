"use client";

import { AtSign, Facebook, Globe, Instagram, Linkedin, Megaphone, Music2, Youtube } from "lucide-react";
import type { SocialPlatform } from "@acme/shared-types";
import { cn } from "@/lib/utils";

/**
 * One icon per platform, in each platform's own colour.
 *
 * Colour rather than a uniform grey because these are recognised by brand: an
 * administrator scanning a list of twelve connections finds the Instagram one
 * by its colour long before they read the label.
 */
const ICONS = {
  FacebookPage: { Icon: Facebook, className: "text-[#1877F2]" },
  InstagramBusiness: { Icon: Instagram, className: "text-[#E4405F]" },
  GoogleBusinessProfile: { Icon: Globe, className: "text-[#4285F4]" },
  GoogleAds: { Icon: Megaphone, className: "text-[#34A853]" },
  YouTubeChannel: { Icon: Youtube, className: "text-[#FF0000]" },
  LinkedInProfile: { Icon: Linkedin, className: "text-[#0A66C2]" },
  LinkedInPage: { Icon: Linkedin, className: "text-[#0A66C2]" },
  // lucide has no TikTok mark; a music note in TikTok's ink.
  TikTokAccount: { Icon: Music2, className: "text-foreground" },
  // A neutral handle sign rather than X's own mark, in X's black ink.
  XAccount: { Icon: AtSign, className: "text-foreground" },
} as const;

export function PlatformIcon({
  platform,
  className,
}: {
  platform: SocialPlatform;
  className?: string;
}) {
  const entry = ICONS[platform] ?? ICONS.FacebookPage;
  const { Icon } = entry;

  return <Icon className={cn(entry.className, className)} aria-hidden />;
}
