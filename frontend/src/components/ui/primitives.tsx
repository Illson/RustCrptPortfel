import * as DialogPrimitive from '@radix-ui/react-dialog'
import * as SelectPrimitive from '@radix-ui/react-select'
import { Slot } from '@radix-ui/react-slot'
import * as TabsPrimitive from '@radix-ui/react-tabs'
import { cva, type VariantProps } from 'class-variance-authority'
import { CheckIcon, ChevronDownIcon, XIcon } from 'lucide-react'
import type { ComponentProps } from 'react'
import { cn } from '@/lib/utils'

const buttonVariants = cva(
  'inline-flex shrink-0 items-center justify-center gap-2 whitespace-nowrap rounded-md text-sm font-medium transition-all outline-none focus-visible:ring-[3px] focus-visible:ring-ring/50 disabled:pointer-events-none disabled:opacity-50 [&_svg]:pointer-events-none [&_svg]:size-4',
  {
    variants: {
      variant: {
        default: 'bg-primary text-primary-foreground shadow-xs hover:bg-primary/90',
        outline: 'border bg-background shadow-xs hover:bg-accent hover:text-accent-foreground dark:border-input dark:bg-input/30',
        secondary: 'bg-secondary text-secondary-foreground hover:bg-secondary/80',
        ghost: 'hover:bg-accent hover:text-accent-foreground',
      },
      size: { default: 'h-9 px-4 py-2', sm: 'h-8 gap-1.5 px-3', icon: 'size-9' },
    },
    defaultVariants: { variant: 'default', size: 'default' },
  },
)

export function Button({ className, variant, size, asChild = false, ...props }: ComponentProps<'button'> & VariantProps<typeof buttonVariants> & { asChild?: boolean }) {
  const Component = asChild ? Slot : 'button'
  return <Component data-slot="button" className={cn(buttonVariants({ variant, size }), className)} {...props} />
}

export function Card({ className, ...props }: ComponentProps<'div'>) {
  return <div data-slot="card" className={cn('flex flex-col gap-5 rounded-xl border bg-card py-5 text-card-foreground shadow-sm', className)} {...props} />
}
export function CardHeader({ className, ...props }: ComponentProps<'div'>) {
  return <div data-slot="card-header" className={cn('grid gap-1.5 px-5', className)} {...props} />
}
export function CardTitle({ className, ...props }: ComponentProps<'h2'>) {
  return <h2 data-slot="card-title" className={cn('font-semibold leading-none', className)} {...props} />
}
export function CardDescription({ className, ...props }: ComponentProps<'p'>) {
  return <p data-slot="card-description" className={cn('text-sm text-muted-foreground', className)} {...props} />
}
export function CardContent({ className, ...props }: ComponentProps<'div'>) {
  return <div data-slot="card-content" className={cn('min-w-0 px-5', className)} {...props} />
}

export function Badge({ className, variant = 'secondary', ...props }: ComponentProps<'span'> & { variant?: 'outline' | 'secondary' | 'default' }) {
  return <span data-slot="badge" className={cn('inline-flex w-fit shrink-0 items-center gap-1 rounded-full border px-2 py-0.5 text-xs font-medium whitespace-nowrap [&_svg]:size-3',
    variant === 'secondary' && 'border-transparent bg-secondary text-secondary-foreground',
    variant === 'default' && 'border-transparent bg-primary text-primary-foreground', className)} {...props} />
}

export function Alert({ className, ...props }: ComponentProps<'div'>) {
  return <div role="alert" data-slot="alert" className={cn('grid grid-cols-[0_1fr] gap-y-0.5 rounded-lg border bg-card px-4 py-3 text-sm text-card-foreground has-[>svg]:grid-cols-[1rem_1fr] has-[>svg]:gap-x-3 [&>svg]:size-4', className)} {...props} />
}
export function AlertTitle({ className, ...props }: ComponentProps<'div'>) {
  return <div className={cn('col-start-2 font-medium', className)} {...props} />
}
export function AlertDescription({ className, ...props }: ComponentProps<'div'>) {
  return <div className={cn('col-start-2 text-muted-foreground', className)} {...props} />
}

export function Skeleton({ className, ...props }: ComponentProps<'div'>) {
  return <div data-slot="skeleton" className={cn('animate-pulse rounded-md bg-accent', className)} {...props} />
}

export function Table({ className, ...props }: ComponentProps<'table'>) {
  return <div data-slot="table-container" className="relative w-full overflow-x-auto"><table data-slot="table" className={cn('w-full caption-bottom text-sm', className)} {...props} /></div>
}
export function TableHeader({ className, ...props }: ComponentProps<'thead'>) {
  return <thead className={cn('[&_tr]:border-b', className)} {...props} />
}
export function TableBody(props: ComponentProps<'tbody'>) { return <tbody className="[&_tr:last-child]:border-0" {...props} /> }
export function TableRow({ className, ...props }: ComponentProps<'tr'>) {
  return <tr className={cn('border-b transition-colors hover:bg-muted/50', className)} {...props} />
}
export function TableHead({ className, ...props }: ComponentProps<'th'>) {
  return <th className={cn('h-10 px-2 text-left align-middle font-medium whitespace-nowrap text-muted-foreground', className)} {...props} />
}
export function TableCell({ className, ...props }: ComponentProps<'td'>) {
  return <td className={cn('p-2 align-middle whitespace-nowrap', className)} {...props} />
}

export const Tabs = TabsPrimitive.Root
export function TabsList({ className, ...props }: ComponentProps<typeof TabsPrimitive.List>) {
  return <TabsPrimitive.List data-slot="tabs-list" className={cn('inline-flex h-9 w-fit items-center justify-center rounded-lg bg-muted p-[3px] text-muted-foreground', className)} {...props} />
}
export function TabsTrigger({ className, ...props }: ComponentProps<typeof TabsPrimitive.Trigger>) {
  return <TabsPrimitive.Trigger className={cn('inline-flex h-full shrink-0 items-center justify-center gap-1.5 rounded-md border border-transparent px-2 py-1 text-sm font-medium whitespace-nowrap outline-none focus-visible:ring-2 focus-visible:ring-ring data-[state=active]:bg-background data-[state=active]:text-foreground data-[state=active]:shadow-sm', className)} {...props} />
}
export function TabsContent({ className, ...props }: ComponentProps<typeof TabsPrimitive.Content>) {
  return <TabsPrimitive.Content className={cn('mt-4 min-w-0 flex-1 outline-none', className)} {...props} />
}

export const Select = SelectPrimitive.Root
export const SelectValue = SelectPrimitive.Value
export function SelectTrigger({ className, children, ...props }: ComponentProps<typeof SelectPrimitive.Trigger>) {
  return <SelectPrimitive.Trigger className={cn('flex h-9 w-full items-center justify-between gap-2 rounded-md border border-input bg-transparent px-3 py-2 text-sm shadow-xs outline-none focus-visible:ring-2 focus-visible:ring-ring', className)} {...props}>
    {children}<SelectPrimitive.Icon asChild><ChevronDownIcon className="size-4 opacity-50" /></SelectPrimitive.Icon>
  </SelectPrimitive.Trigger>
}
export function SelectContent({ children, ...props }: ComponentProps<typeof SelectPrimitive.Content>) {
  return <SelectPrimitive.Portal><SelectPrimitive.Content position="popper" sideOffset={4} className="z-50 min-w-(--radix-select-trigger-width) rounded-md border bg-popover text-popover-foreground shadow-md" {...props}>
    <SelectPrimitive.Viewport className="p-1">{children}</SelectPrimitive.Viewport>
  </SelectPrimitive.Content></SelectPrimitive.Portal>
}
export function SelectItem({ children, ...props }: ComponentProps<typeof SelectPrimitive.Item>) {
  return <SelectPrimitive.Item className="relative flex cursor-default items-center rounded-sm py-1.5 pr-8 pl-2 text-sm outline-none focus:bg-accent" {...props}>
    <SelectPrimitive.ItemText>{children}</SelectPrimitive.ItemText><SelectPrimitive.ItemIndicator className="absolute right-2"><CheckIcon className="size-4" /></SelectPrimitive.ItemIndicator>
  </SelectPrimitive.Item>
}

export const Dialog = DialogPrimitive.Root
export const DialogTrigger = DialogPrimitive.Trigger
export const DialogTitle = DialogPrimitive.Title
export const DialogDescription = DialogPrimitive.Description
export const Sheet = DialogPrimitive.Root
export const SheetTrigger = DialogPrimitive.Trigger
export const SheetTitle = DialogPrimitive.Title
export const SheetDescription = DialogPrimitive.Description

function Layer({ children, className, ...props }: ComponentProps<typeof DialogPrimitive.Content>) {
  return <DialogPrimitive.Portal>
    <DialogPrimitive.Overlay className="fixed inset-0 z-50 bg-black/50" />
    <DialogPrimitive.Content className={cn('fixed z-50 bg-background shadow-lg', className)} {...props}>
      {children}
      <DialogPrimitive.Close className="absolute top-4 right-4 rounded-sm p-1 opacity-70 hover:opacity-100 focus-visible:ring-2 focus-visible:ring-ring">
        <XIcon className="size-4" /><span className="sr-only">Закрыть</span>
      </DialogPrimitive.Close>
    </DialogPrimitive.Content>
  </DialogPrimitive.Portal>
}
export function DialogContent({ className, ...props }: ComponentProps<typeof DialogPrimitive.Content>) {
  return <Layer className={cn('top-1/2 left-1/2 grid max-h-[85dvh] w-[calc(100%-2rem)] max-w-lg -translate-1/2 gap-4 overflow-auto rounded-lg border p-6', className)} {...props} />
}
export function SheetContent({ className, ...props }: ComponentProps<typeof DialogPrimitive.Content>) {
  return <Layer className={cn('inset-y-0 left-0 w-4/5 max-w-xs border-r', className)} {...props} />
}

export function Sidebar({ className, ...props }: ComponentProps<'aside'>) {
  return <aside data-slot="sidebar" className={cn('flex h-full w-60 flex-col border-sidebar-border bg-sidebar text-sidebar-foreground', className)} {...props} />
}
export function Input({ className, ...props }: ComponentProps<'input'>) {
  return <input data-slot="input" className={cn('flex h-9 w-full min-w-0 rounded-md border border-input bg-transparent px-3 py-1 text-sm shadow-xs outline-none focus-visible:ring-2 focus-visible:ring-ring', className)} {...props} />
}
