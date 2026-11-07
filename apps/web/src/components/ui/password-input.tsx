"use client";

import * as React from "react";
import { Eye, EyeOff } from "lucide-react";
import { Input, type InputProps } from "@/components/ui/input";
import { cn } from "@/lib/utils";

/**
 * A password field you can read back.
 *
 * Typing a password blind is where sign-in failures come from, and the person
 * who most needs to check what they typed is the one on a phone keyboard with
 * a long password from a manager. Getting it wrong twice on this app is worse
 * than an annoyance: it leads people to the reset flow, which signs every one
 * of their sessions out.
 *
 * Deliberate details, each of which is a way this control is commonly broken:
 *
 * - `type="button"`. A bare <button> inside a form defaults to `submit`, so
 *   revealing the password would submit half-filled credentials.
 * - The state is announced. A sighted user sees the eye change; `aria-pressed`
 *   and a label that names the *action* are what tell everyone else.
 * - Right padding on the input, so a long password does not run underneath
 *   the button.
 * - It starts hidden, always. There is no memory of the last choice — this is
 *   a screen people use in a dispensary with a queue behind them.
 */
export interface PasswordInputProps extends InputProps {
  /**
   * What this particular field holds, for the reveal button's label.
   *
   * Three forms in the app show a password and a confirmation side by side.
   * With a fixed label both buttons are announced as "Show password", so
   * anyone navigating by name hears the same control twice and cannot tell
   * which field they are about to expose. Pass "confirm password" on the
   * second one.
   */
  revealLabel?: string;
}

export const PasswordInput = React.forwardRef<HTMLInputElement, PasswordInputProps>(
  ({ className, disabled, revealLabel = "password", ...props }, ref) => {
    const [visible, setVisible] = React.useState(false);

    return (
      <div className="relative">
        <Input
          {...props}
          ref={ref}
          disabled={disabled}
          type={visible ? "text" : "password"}
          className={cn("pr-10", className)}
        />

        <button
          type="button"
          onClick={() => setVisible((v) => !v)}
          disabled={disabled}
          // Names the action rather than the state: "Show password" is what
          // pressing it does. Screen readers read the label and the pressed
          // state together, so naming the state here would contradict it.
          aria-label={`${visible ? "Hide" : "Show"} ${revealLabel}`}
          aria-pressed={visible}
          // Ties the button to the field it acts on, which is what lets a
          // screen reader place it when the two are read out separately.
          aria-controls={props.id}
          className={cn(
            "absolute right-0 top-0 flex h-10 w-10 items-center justify-center rounded-md",
            "text-muted-foreground transition-colors hover:text-foreground",
            "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background",
            "disabled:cursor-not-allowed disabled:opacity-50",
          )}
        >
          {visible ? (
            <EyeOff className="h-4 w-4" aria-hidden />
          ) : (
            <Eye className="h-4 w-4" aria-hidden />
          )}
        </button>
      </div>
    );
  },
);
PasswordInput.displayName = "PasswordInput";
