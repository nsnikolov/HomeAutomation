import type { ComponentProps } from 'react'
import { cva, type VariantProps } from 'class-variance-authority'
import { cn } from './utils'

const buttonVariants = cva('button', {
  variants: { variant: { default: 'button-primary', outline: 'button-outline', ghost: 'button-ghost' } },
  defaultVariants: { variant: 'default' },
})

export function Button({ className, variant, ...props }: ComponentProps<'button'> & VariantProps<typeof buttonVariants>) {
  return <button data-slot="button" className={cn(buttonVariants({ variant }), className)} {...props} />
}

export function Card({ className, ...props }: ComponentProps<'section'>) {
  return <section data-slot="card" className={cn('card bg-card text-card-foreground', className)} {...props} />
}

export function Badge({ className, ...props }: ComponentProps<'span'>) {
  return <span data-slot="badge" className={cn('badge', className)} {...props} />
}

export function Input({ className, ...props }: ComponentProps<'input'>) {
  return <input data-slot="input" className={cn('input', className)} {...props} />
}
